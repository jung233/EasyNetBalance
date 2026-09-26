using System.Net.NetworkInformation;
using System.Net;
using System.Collections.Concurrent;
using EasyBalance.Service.Health;
using EasyBalance.Shared;
using Microsoft.Extensions.Logging;

namespace EasyBalance.Service;

/// <summary>Shares probes by adapter/address family while maintaining an independent health state for each policy.</summary>
public sealed class HealthCoordinator : IDisposable
{
    private readonly IAdapterProvider _provider;
    private readonly IInterfaceProbe _probe;
    private readonly ILogger<HealthCoordinator> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<(string Id, AddressFamilyKind Family), HealthStateMachine> _states = new();
    private readonly ConcurrentDictionary<(string Id, AddressFamilyKind Family, string PolicyId), HealthStateMachine> _policyStates = new();
    private readonly ConcurrentDictionary<(string Id, AddressFamilyKind Family), DateTimeOffset> _due = new();
    private readonly ConcurrentDictionary<(string Id, AddressFamilyKind Family), DateTimeOffset> _lastProbe = new();
    private readonly Dictionary<(string Id, AddressFamilyKind Family), int> _endpointIndex = new();
    private IReadOnlyList<NetworkAdapterInfo> _adapters = Array.Empty<NetworkAdapterInfo>();
    private AppSettings _settings = new();
    private string _healthThresholdSignature = string.Empty;
    private volatile bool _refresh = true;
    private volatile bool _suspended;
    private long _probes;
    private long _successes;
    private long _failures;

    public event Action? Changed;
    public IReadOnlyList<NetworkAdapterInfo> Adapters => _adapters;
    public long ProbeCount => Interlocked.Read(ref _probes);
    public long SuccessfulProbes => Interlocked.Read(ref _successes);
    public long FailedProbes => Interlocked.Read(ref _failures);

    public HealthCoordinator(IAdapterProvider provider, ILogger<HealthCoordinator> logger)
        : this(provider, new BoundInterfaceProbe(), logger) { }

    public HealthCoordinator(IAdapterProvider provider, IInterfaceProbe probe, ILogger<HealthCoordinator> logger)
    {
        _provider = provider;
        _probe = probe;
        _logger = logger;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void Configure(AppSettings settings)
    {
        var signature = string.Join("|", new[] { settings.DefaultPolicy }
            .Concat(settings.Policies.Where(policy => policy.Enabled))
            .OrderBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase)
            .Select(policy => $"{policy.Id}:{policy.FailureThreshold}:{policy.RecoveryThreshold}:{policy.RecoveryStabilization.Ticks}"));
        if (!string.Equals(signature, _healthThresholdSignature, StringComparison.Ordinal))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var state in _states.Values) state.Reset(now);
            _policyStates.Clear();
            _healthThresholdSignature = signature;
        }

        _settings = settings;
        RequestRefresh();
    }

    public HealthState GetState(string interfaceId, AddressFamilyKind family) =>
        _states.TryGetValue((interfaceId, family), out var machine) ? machine.State : HealthState.Unavailable;

    public HealthState GetState(string interfaceId, AddressFamilyKind family, string policyId)
    {
        if (string.Equals(policyId, _settings.DefaultPolicy.Id, StringComparison.OrdinalIgnoreCase))
        {
            return GetState(interfaceId, family);
        }

        var key = (interfaceId, family, NormalizePolicyId(policyId));
        return _policyStates.TryGetValue(key, out var machine) ? machine.State : HealthState.Unavailable;
    }

    public DateTimeOffset? GetLastProbeTime(string interfaceId, AddressFamilyKind family) =>
        _lastProbe.TryGetValue((interfaceId, family), out var value) ? value : null;

    public void Suspend()
    {
        _suspended = true;
        Wake();
    }

    public void Resume()
    {
        _suspended = false;
        foreach (var state in _states.Values) state.Reset(DateTimeOffset.UtcNow);
        foreach (var state in _policyStates.Values) state.Reset(DateTimeOffset.UtcNow);
        RequestRefresh();
    }

    public void RequestRefresh()
    {
        _refresh = true;
        Wake();
    }

    public void RequestProbe(string interfaceId, AddressFamilyKind family)
    {
        _due[(interfaceId, family)] = DateTimeOffset.UtcNow;
        Wake();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastAwake = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var current = DateTimeOffset.UtcNow;
            if (current - lastAwake > TimeSpan.FromMinutes(2))
            {
                // Long timer gap indicates sleep/resume or a paused service. Discard stale health.
                foreach (var state in _states.Values) state.Reset(current);
                foreach (var state in _policyStates.Values) state.Reset(current);
                _refresh = true;
                Changed?.Invoke();
            }
            lastAwake = current;
            if (_suspended)
            {
                await _wake.WaitAsync(cancellationToken);
                continue;
            }
            if (_refresh)
            {
                _refresh = false;
                // Coalesce Windows' burst of address/route notifications.
                await Task.Delay(300, cancellationToken);
                await RefreshAsync(cancellationToken);
            }
            var now = DateTimeOffset.UtcNow;
            var next = _due.OrderBy(pair => pair.Value).FirstOrDefault();
            if (next.Key.Id is null)
            {
                await _wake.WaitAsync(cancellationToken);
                continue;
            }
            if (next.Value > now)
            {
                var delay = next.Value - now;
                await _wake.WaitAsync(delay, cancellationToken);
                continue;
            }
            _due.TryRemove(next.Key, out _);
            var adapter = _adapters.FirstOrDefault(value => value.Id == next.Key.Id);
            if (adapter is null) continue;
            await ProbeOneAsync(adapter, next.Key.Family, cancellationToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var adapters = await _provider.GetAdaptersAsync(cancellationToken);
        foreach (var adapter in adapters)
            if (_settings.InterfaceUsabilityOverrides.TryGetValue(adapter.Id, out var allowed))
                adapter.IsUserAllowed = allowed;
        var visible = adapters.Where(value => value.IsUserAllowed).ToArray();
        var ids = visible.Select(value => value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var policyIds = GetEnabledNonDefaultPolicies().Select(policy => NormalizePolicyId(policy.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _states.Keys.Where(key => !ids.Contains(key.Id)).ToArray())
        {
            _states[key].MarkUnavailable(DateTimeOffset.UtcNow);
            _due.TryRemove(key, out _);
        }
        foreach (var key in _policyStates.Keys.Where(key => !ids.Contains(key.Id) || !policyIds.Contains(key.PolicyId)).ToArray())
        {
            if (_policyStates.TryRemove(key, out var state)) state.MarkUnavailable(DateTimeOffset.UtcNow);
        }
        _adapters = adapters;
        foreach (var adapter in visible)
            foreach (var family in Enum.GetValues<AddressFamilyKind>())
            {
                var key = (adapter.Id, family);
                _states.TryAdd(key, new HealthStateMachine());
                foreach (var policy in GetEnabledNonDefaultPolicies())
                    _policyStates.TryAdd((adapter.Id, family, NormalizePolicyId(policy.Id)), new HealthStateMachine());
                _due[key] = DateTimeOffset.UtcNow;
            }
        Changed?.Invoke();
    }

    private async Task ProbeOneAsync(NetworkAdapterInfo adapter, AddressFamilyKind family,
        CancellationToken cancellationToken)
    {
        var key = (adapter.Id, family);
        var machine = _states[key];
        var now = DateTimeOffset.UtcNow;
        var addresses = family == AddressFamilyKind.IPv4 ? adapter.IPv4Addresses : adapter.IPv6Addresses;
        var gateways = family == AddressFamilyKind.IPv4 ? adapter.IPv4Gateways : adapter.IPv6Gateways;
        var hasUsableAddress = addresses.Any(value => IPAddress.TryParse(value, out var address) &&
            !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
            (family != AddressFamilyKind.IPv6 || !address.IsIPv6LinkLocal));
        if (adapter.OperationalStatus != OperationalStatus.Up || !hasUsableAddress || gateways.Count == 0 ||
            family == AddressFamilyKind.IPv6 && !_settings.Ipv6Enabled)
        {
            machine.MarkUnavailable(now);
            MarkPolicyStatesUnavailable(adapter.Id, family, now);
            _lastProbe[key] = now;
            SetAdapterHealth(adapter, family, machine, null, now);
            _due[key] = now + TimeSpan.FromSeconds(10);
            Changed?.Invoke();
            return;
        }

        var endpoints = _settings.ProbeEndpoints.Where(value => value.Enabled &&
            (family == AddressFamilyKind.IPv4 ? value.RequireIPv4 : value.RequireIPv6) &&
            Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps).ToArray();
        if (endpoints.Length == 0)
        {
            machine.MarkUnavailable(now);
            MarkPolicyStatesUnavailable(adapter.Id, family, now);
            _lastProbe[key] = now;
            _logger.LogWarning("No enabled HTTPS probe endpoint for {Family}", family);
            SetAdapterHealth(adapter, family, machine, null, now);
            _due[key] = now + TimeSpan.FromSeconds(10);
            Changed?.Invoke();
            return;
        }

        var position = _endpointIndex.GetValueOrDefault(key) % endpoints.Length;
        _endpointIndex[key] = position + 1;
        var latency = await ProbeEndpointAsync(adapter, family, new Uri(endpoints[position].Url), cancellationToken);
        Interlocked.Increment(ref _probes);
        if (latency is null) Interlocked.Increment(ref _failures); else Interlocked.Increment(ref _successes);
        var success = latency is not null;
        if (!success && endpoints.Length > 1)
        {
            var confirmations = 0;
            var total = Math.Min(3, endpoints.Length);
            for (var index = 1; index < total; index++)
            {
                var result = await ProbeEndpointAsync(adapter, family,
                    new Uri(endpoints[(position + index) % endpoints.Length].Url), cancellationToken);
                Interlocked.Increment(ref _probes);
                if (result is null) Interlocked.Increment(ref _failures);
                else { Interlocked.Increment(ref _successes); confirmations++; latency ??= result; }
            }
            success = confirmations + (success ? 1 : 0) >= Math.Min(2, total);
        }
        var old = machine.State;
        machine.Observe(success, Math.Max(1, _settings.DefaultPolicy.FailureThreshold),
            Math.Max(1, _settings.DefaultPolicy.RecoveryThreshold),
            _settings.DefaultPolicy.RecoveryStabilization, now);
        var policyMachines = new List<HealthStateMachine>();
        foreach (var policy in GetEnabledNonDefaultPolicies())
        {
            if (_policyStates.TryGetValue((adapter.Id, family, NormalizePolicyId(policy.Id)), out var policyMachine))
            {
                policyMachine.Observe(success, Math.Max(1, policy.FailureThreshold),
                    Math.Max(1, policy.RecoveryThreshold), policy.RecoveryStabilization, now);
                policyMachines.Add(policyMachine);
            }
        }
        SetAdapterHealth(adapter, family, machine, latency, now);
        _lastProbe[key] = DateTimeOffset.UtcNow;
        if (old != machine.State)
            _logger.LogInformation("{Adapter} {Family}: {Old} -> {New}", adapter.Name, family, old, machine.State);
        var probeState = policyMachines.Any(state => state.State is HealthState.Suspect or HealthState.Recovering)
            ? HealthState.Suspect
            : policyMachines.Any(state => state.State == HealthState.Down) ? HealthState.Down : machine.State;
        _due[key] = now + (probeState switch
        {
            HealthState.Healthy => _settings.HealthyProbeInterval,
            HealthState.Suspect => _settings.SuspectProbeInterval,
            HealthState.Down => _settings.DownProbeInterval,
            HealthState.Recovering => _settings.SuspectProbeInterval,
            _ => TimeSpan.FromSeconds(10)
        });
        Changed?.Invoke();
    }

    private async Task<TimeSpan?> ProbeEndpointAsync(
        NetworkAdapterInfo adapter,
        AddressFamilyKind family,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ProbeAsync(adapter, family, endpoint, _settings.ProbeTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Probe failed unexpectedly for {Adapter} {Family} via {Endpoint}",
                adapter.Id, family, endpoint.Host);
            return null;
        }
    }

    private void MarkPolicyStatesUnavailable(string interfaceId, AddressFamilyKind family, DateTimeOffset now)
    {
        foreach (var policy in GetEnabledNonDefaultPolicies())
        {
            if (_policyStates.TryGetValue((interfaceId, family, NormalizePolicyId(policy.Id)), out var machine))
            {
                machine.MarkUnavailable(now);
            }
        }
    }

    private IEnumerable<RoutingPolicy> GetEnabledNonDefaultPolicies() =>
        _settings.Policies.Where(policy => policy.Enabled &&
            !string.Equals(policy.Id, _settings.DefaultPolicy.Id, StringComparison.OrdinalIgnoreCase));

    private static string NormalizePolicyId(string policyId) => policyId.ToLowerInvariant();

    private static void SetAdapterHealth(NetworkAdapterInfo adapter, AddressFamilyKind family,
        HealthStateMachine machine, TimeSpan? latency, DateTimeOffset now)
    {
        if (family == AddressFamilyKind.IPv4) { adapter.IPv4Health = machine.State; adapter.IPv4Latency = latency; }
        else { adapter.IPv6Health = machine.State; adapter.IPv6Latency = latency; }
        adapter.LastProbeTime = now;
        adapter.LastStateChange = machine.LastChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs args) => RequestRefresh();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => RequestRefresh();
    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* A concurrent event already woke the scheduler. */ }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _wake.Dispose();
    }
}
