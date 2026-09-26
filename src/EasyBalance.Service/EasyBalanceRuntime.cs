using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using EasyBalance.Service.Core;
using EasyBalance.Service.Health;
using EasyBalance.Service.Logging;
using EasyBalance.Shared;
using Microsoft.Extensions.Logging;

namespace EasyBalance.Service;

public sealed class EasyBalanceRuntime(
    HealthCoordinator health,
    SingBoxManager core,
    IAdapterProvider adapters,
    ILoggerFactory loggerFactory,
    ILogger<EasyBalanceRuntime> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AtomicJsonFileStore _store = new();
    private readonly Dictionary<(string Policy, AddressFamilyKind Family), PolicyFamilyRuntime> _policyRuntime = new();
    private readonly Dictionary<string, string> _generatedOutboundNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<RuntimeLogEntry> _logs = new();
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyBalance");
    private AppSettings _settings = AppSettings.CreateDefault();
    private long _failovers;
    private long _failbacks;
    private DateTimeOffset? _lastCoreStart;

    private string SettingsPath => Path.Combine(_dataDirectory, "settings.json");
    private string GeneratedConfigPath => Path.Combine(_dataDirectory, "generated", "sing-box.json");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        EnsureDataDirectory();
        loggerFactory.AddProvider(new RollingFileLoggerProvider());
        if (File.Exists(SettingsPath))
            _settings = await _store.ReadAsync(SettingsPath, EasyBalanceJsonContext.Default.AppSettings, cancellationToken)
                ?? AppSettings.CreateDefault();
        else
            await _store.WriteAsync(SettingsPath, _settings, EasyBalanceJsonContext.Default.AppSettings, cancellationToken);
        ValidateSettings(_settings);
        health.Configure(_settings);
        health.Changed += OnHealthChanged;
        core.LogReceived += (_, entry) => Log(entry.Level, "sing-box", entry.Message);
        if (_settings.Enabled)
        {
            try
            {
                var currentAdapters = await GetAdaptersAsync(cancellationToken);
                await core.StartAsync(_settings, currentAdapters, cancellationToken);
                RememberGeneratedOutboundNames(currentAdapters);
            }
            catch (Exception exception) { Log("Error", "Core", exception.Message); logger.LogError(exception, "Unable to start sing-box"); }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        health.Changed -= OnHealthChanged;
        await core.StopAsync(cancellationToken);
    }

    private void OnHealthChanged() => _ = EvaluateSafelyAsync();

    private async Task EvaluateSafelyAsync()
    {
        try { await EvaluatePoliciesAsync(CancellationToken.None); }
        catch (Exception exception) { logger.LogError(exception, "Policy evaluation failed"); }
    }

    private async Task EvaluatePoliciesAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || !core.Snapshot.CoreRunning) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentAdapters = health.Adapters;
            if (RequiresGeneratedConfigRefresh(_settings, currentAdapters))
            {
                await core.ApplyConfigurationAsync(_settings, currentAdapters, cancellationToken);
                RememberGeneratedOutboundNames(currentAdapters);
                _policyRuntime.Clear();
            }
            if (_lastCoreStart != core.Snapshot.StartedAt)
            {
                _policyRuntime.Clear();
                _lastCoreStart = core.Snapshot.StartedAt;
            }
            foreach (var policy in AllPolicies())
            {
                if (!policy.Enabled || string.IsNullOrWhiteSpace(policy.PrimaryInterfaceId)) continue;
                foreach (var family in Enum.GetValues<AddressFamilyKind>())
                {
                    if (family == AddressFamilyKind.IPv6 && !_settings.Ipv6Enabled) continue;
                    var key = (policy.Id, family);
                    if (!_policyRuntime.TryGetValue(key, out var state))
                        _policyRuntime[key] = state = new PolicyFamilyRuntime();
                    state.Initialize(policy.PrimaryInterfaceId);
                    var target = state.Decide(policy,
                        health.GetState(policy.PrimaryInterfaceId, family, policy.Id),
                        policy.FallbackInterfaceId is null ? HealthState.Unavailable : health.GetState(policy.FallbackInterfaceId, family, policy.Id),
                        DateTimeOffset.UtcNow);
                    if (target is null) continue;
                    var selector = SingBoxConfigGenerator.SelectorTag(policy.Id,
                        family == AddressFamilyKind.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6);
                    var outbound = SingBoxConfigGenerator.OutboundTag(target);
                    try
                    {
                        await core.Control.SwitchSelectorAsync(selector, outbound, cancellationToken);
                        var confirmed = await core.Control.GetSelectorStateAsync(selector, cancellationToken);
                        if (confirmed?.SelectedOutboundTag != outbound)
                            throw new InvalidOperationException($"Selector {selector} did not confirm {outbound}.");
                        var failback = target == policy.PrimaryInterfaceId;
                        state.ConfirmSwitch(target, DateTimeOffset.UtcNow);
                        if (failback) Interlocked.Increment(ref _failbacks);
                        else Interlocked.Increment(ref _failovers);
                        Log("Information", "Failover", $"{policy.Name} {family}: {target}");
                    }
                    catch (Exception exception)
                    {
                        Log("Error", "Failover", $"{policy.Name} {family}: {exception.Message}");
                        logger.LogError(exception, "Selector switch failed for {Policy} {Family}", policy.Id, family);
                    }
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<PipeResponse> HandleAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Method switch
            {
                "GetStatus" => Ok(GetStatus(), ServiceJsonContext.Default.RuntimeStatus),
                "GetAdapters" => Ok((await GetAdaptersAsync(cancellationToken)).ToList(), EasyBalanceJsonContext.Default.ListNetworkAdapterInfo),
                "GetRules" => Ok(_settings.ApplicationRules, EasyBalanceJsonContext.Default.ListApplicationRule),
                "GetPolicies" => Ok(AllPolicies().ToList(), EasyBalanceJsonContext.Default.ListRoutingPolicy),
                "GetSettings" => Ok(_settings, EasyBalanceJsonContext.Default.AppSettings),
                "GetLogs" => Ok(GetLogs(), ServiceJsonContext.Default.ListRuntimeLogEntry),
                "GetDiagnostics" => Ok(GetDiagnostics(), ServiceJsonContext.Default.RuntimeDiagnostics),
                "GetProcesses" => Ok(GetProcesses(), ServiceJsonContext.Default.ListProcessSnapshot),
                "GetGeneratedConfig" => Ok(new FileContentResult { Json = await ReadRedactedConfigAsync(cancellationToken) }, ServiceJsonContext.Default.FileContentResult),
                "SaveRule" => await SaveRuleAsync(Read<ApplicationRule>(request.Payload, EasyBalanceJsonContext.Default.ApplicationRule), cancellationToken),
                "DeleteRule" => await DeleteRuleAsync(GetRequiredString(request.Payload, "id"), cancellationToken),
                "SavePolicy" => await SavePolicyAsync(Read<RoutingPolicy>(request.Payload, EasyBalanceJsonContext.Default.RoutingPolicy), cancellationToken),
                "DeletePolicy" => await DeletePolicyAsync(GetRequiredString(request.Payload, "id"), cancellationToken),
                "SetDefaultPolicy" => await SetDefaultPolicyAsync(GetRequiredString(request.Payload, "id"), cancellationToken),
                "SaveSettings" => await ApplySettingsAsync(Read<AppSettings>(request.Payload, EasyBalanceJsonContext.Default.AppSettings), cancellationToken),
                "SetInterfaceUsability" => await SetInterfaceUsabilityAsync(request.Payload, cancellationToken),
                "EnableRouting" => await SetRoutingAsync(true, cancellationToken),
                "DisableRouting" => await SetRoutingAsync(false, cancellationToken),
                "RestartCore" => await RestartCoreAsync(cancellationToken),
                "ValidateConfig" => await ValidateConfigAsync(cancellationToken),
                "TestInterface" => await TestInterfaceAsync(request.Payload, cancellationToken),
                "ExportDiagnostics" => await ExportDiagnosticsAsync(request.Payload, cancellationToken),
                _ => Fail($"Unknown method: {request.Method}")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or IOException)
        {
            return Fail(exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "IPC method {Method} failed", request.Method);
            return Fail("The service could not complete this request. See the service log for details.");
        }
    }

    private async Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(
        CancellationToken cancellationToken,
        AppSettings? settings = null)
    {
        var items = await adapters.GetAdaptersAsync(cancellationToken);
        var effectiveSettings = settings ?? _settings;
        foreach (var adapter in items)
        {
            if (effectiveSettings.InterfaceUsabilityOverrides.TryGetValue(adapter.Id, out var allowed)) adapter.IsUserAllowed = allowed;
            adapter.IPv4Health = health.GetState(adapter.Id, AddressFamilyKind.IPv4);
            adapter.IPv6Health = health.GetState(adapter.Id, AddressFamilyKind.IPv6);
            var observed = health.Adapters.FirstOrDefault(value => value.Id == adapter.Id);
            if (observed is not null)
            {
                adapter.IPv4Latency = observed.IPv4Latency;
                adapter.IPv6Latency = observed.IPv6Latency;
                adapter.LastProbeTime = observed.LastProbeTime;
                adapter.LastStateChange = observed.LastStateChange;
            }
        }
        return items;
    }

    private RuntimeStatus GetStatus()
    {
        var snapshot = core.Snapshot;
        return new RuntimeStatus
        {
            RoutingEnabled = _settings.Enabled,
            CoreRunning = snapshot.CoreRunning,
            CoreFaulted = snapshot.Faulted,
            Version = snapshot.Version,
            Uptime = snapshot.Uptime,
            LastError = snapshot.LastError,
            LastExitCode = snapshot.LastExitCode,
            Policies = AllPolicies().Select(policy =>
            {
                _policyRuntime.TryGetValue((policy.Id, AddressFamilyKind.IPv4), out var v4);
                _policyRuntime.TryGetValue((policy.Id, AddressFamilyKind.IPv6), out var v6);
                return new PolicyRouteStatus
                {
                    PolicyId = policy.Id, Name = policy.Name,
                    ActiveIPv4Interface = v4?.ActiveInterfaceId,
                    ActiveIPv6Interface = v6?.ActiveInterfaceId,
                    NoHealthyIPv4Interface = v4?.NoHealthyInterface ?? false,
                    NoHealthyIPv6Interface = v6?.NoHealthyInterface ?? false
                };
            }).ToList()
        };
    }

    private RuntimeDiagnostics GetDiagnostics()
    {
        using var process = Process.GetCurrentProcess();
        var snapshot = core.Snapshot;
        var policies = AllPolicies().Count(value => value.Enabled);
        var families = _settings.Ipv6Enabled ? 2 : 1;
        var healthPolicyContexts = 1 + _settings.Policies.Count(value => value.Enabled &&
            !string.Equals(value.Id, _settings.DefaultPolicy.Id, StringComparison.OrdinalIgnoreCase));
        var result = new RuntimeDiagnostics
        {
            HealthProbes = health.ProbeCount, SuccessfulProbes = health.SuccessfulProbes,
            FailedProbes = health.FailedProbes, FailoverCount = Interlocked.Read(ref _failovers),
            FailbackCount = Interlocked.Read(ref _failbacks), RulesCount = _settings.ApplicationRules.Count,
            PoliciesCount = AllPolicies().Count(), HealthContexts = health.Adapters.Count(value => value.IsUserAllowed) * families * healthPolicyContexts,
            ServicePrivateBytes = process.PrivateMemorySize64, ServiceWorkingSetBytes = process.WorkingSet64,
            ServiceCpuSeconds = process.TotalProcessorTime.TotalSeconds,
            CoreRunning = snapshot.CoreRunning, RoutingEnabled = _settings.Enabled,
            GeneratedRouteRules = _settings.ApplicationRules.Count(value => value.Enabled) * families + families,
            GeneratedSelectors = policies * families,
            GeneratedDirectOutbounds = health.Adapters.Count(value => value.IsUserAllowed)
        };
        if (snapshot.ProcessId is { } corePid)
        {
            try
            {
                using var child = Process.GetProcessById(corePid);
                result.SingBoxPrivateBytes = child.PrivateMemorySize64;
                result.SingBoxWorkingSetBytes = child.WorkingSet64;
                result.SingBoxCpuSeconds = child.TotalProcessorTime.TotalSeconds;
            }
            catch (ArgumentException) { /* Core exited between snapshot and sample. */ }
        }
        var uiProcesses = Process.GetProcessesByName("EasyBalance.UI");
        try
        {
            if (uiProcesses.FirstOrDefault() is { } ui)
            {
                result.UiPrivateBytes = ui.PrivateMemorySize64;
                result.UiWorkingSetBytes = ui.WorkingSet64;
                result.UiCpuSeconds = ui.TotalProcessorTime.TotalSeconds;
            }
        }
        finally { foreach (var ui in uiProcesses) ui.Dispose(); }
        return result;
    }

    private IEnumerable<RoutingPolicy> AllPolicies()
    {
        yield return _settings.DefaultPolicy;
        foreach (var policy in _settings.Policies.Where(value => value.Id != _settings.DefaultPolicy.Id)) yield return policy;
    }

    private async Task<PipeResponse> SaveRuleAsync(ApplicationRule rule, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rule.ExecutablePath) && string.IsNullOrWhiteSpace(rule.ProcessName)) return Fail("Choose an executable path or process name.");
        if (!AllPolicies().Any(value => value.Id == rule.PolicyId)) return Fail("The selected policy does not exist.");
        var draft = CloneSettings();
        draft.ApplicationRules.RemoveAll(value => value.Id == rule.Id);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        draft.ApplicationRules.Add(rule);
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> DeleteRuleAsync(string id, CancellationToken cancellationToken)
    {
        var draft = CloneSettings();
        draft.ApplicationRules.RemoveAll(value => value.Id == id);
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> SavePolicyAsync(RoutingPolicy policy, CancellationToken cancellationToken)
    {
        var draft = CloneSettings();
        if (policy.Id == draft.DefaultPolicy.Id) draft.DefaultPolicy = policy;
        else { draft.Policies.RemoveAll(value => value.Id == policy.Id); draft.Policies.Add(policy); }
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> DeletePolicyAsync(string id, CancellationToken cancellationToken)
    {
        if (_settings.DefaultPolicy.Id == id) return Fail("Default policy cannot be deleted.");
        if (_settings.ApplicationRules.Any(value => value.PolicyId == id)) return Fail("Policy is in use by an application rule.");
        var draft = CloneSettings();
        draft.Policies.RemoveAll(value => value.Id == id);
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> SetDefaultPolicyAsync(string id, CancellationToken cancellationToken)
    {
        var draft = CloneSettings();
        var selected = draft.Policies.FirstOrDefault(value => value.Id == id);
        if (selected is null) return Fail("Policy not found.");
        draft.Policies.Remove(selected);
        draft.Policies.Add(draft.DefaultPolicy);
        draft.DefaultPolicy = selected;
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> SetInterfaceUsabilityAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var id = GetRequiredString(payload, "interfaceId");
        var allowed = GetRequiredBool(payload, "allowed");
        var draft = CloneSettings();
        draft.InterfaceUsabilityOverrides[id] = allowed;
        var response = await ApplySettingsAsync(draft, cancellationToken);
        if (response.Success) health.RequestRefresh();
        return response;
    }

    private async Task<PipeResponse> SetRoutingAsync(bool enabled, CancellationToken cancellationToken)
    {
        var draft = CloneSettings();
        draft.Enabled = enabled;
        return await ApplySettingsAsync(draft, cancellationToken);
    }

    private async Task<PipeResponse> ApplySettingsAsync(AppSettings draft, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ValidateSettings(draft);
            var current = _settings;
            var currentAdapters = await GetAdaptersAsync(cancellationToken, draft);
            if (draft.Enabled)
            {
                if (current.Enabled) await core.ApplyConfigurationAsync(draft, currentAdapters, cancellationToken);
                else await core.StartAsync(draft, currentAdapters, cancellationToken);
            }
            else if (current.Enabled) await core.StopAsync(cancellationToken);
            await _store.WriteAsync(SettingsPath, draft, EasyBalanceJsonContext.Default.AppSettings, cancellationToken);
            _settings = draft;
            _policyRuntime.Clear();
            _lastCoreStart = core.Snapshot.StartedAt;
            if (draft.Enabled) RememberGeneratedOutboundNames(currentAdapters);
            health.Configure(draft);
            Log("Information", "Settings", "Configuration saved.");
            return new PipeResponse { Success = true };
        }
        finally { _gate.Release(); }
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.HealthyProbeInterval < TimeSpan.FromSeconds(5) ||
            settings.SuspectProbeInterval < TimeSpan.FromMilliseconds(500) ||
            settings.DownProbeInterval < TimeSpan.FromSeconds(2) ||
            settings.ProbeTimeout < TimeSpan.FromMilliseconds(500) || settings.ProbeTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Probe timing is outside the supported range.");
        var policies = new[] { settings.DefaultPolicy }.Concat(settings.Policies).ToArray();
        if (policies.Any(value => string.IsNullOrWhiteSpace(value.Id)) ||
            policies.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Policy IDs must be nonempty and unique, ignoring case.");
        if (policies.Any(value => value.FailureThreshold < 1 || value.RecoveryThreshold < 1 ||
                                  value.RecoveryStabilization < TimeSpan.Zero || value.MinimumSwitchHoldTime < TimeSpan.Zero))
            throw new ArgumentException("Policy health thresholds and stabilization times must be nonnegative, with thresholds greater than zero.");
        if (settings.ApplicationRules.Any(value => string.IsNullOrWhiteSpace(value.Id)) ||
            settings.ApplicationRules.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Rule IDs must be nonempty and unique, ignoring case.");
        foreach (var endpoint in settings.ProbeEndpoints)
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"Probe endpoint must be HTTPS: {endpoint.Name}");
    }

    private void RememberGeneratedOutboundNames(IEnumerable<NetworkAdapterInfo> currentAdapters)
    {
        _generatedOutboundNames.Clear();
        foreach (var adapter in currentAdapters.Where(value => value.IsUserAllowed))
        {
            _generatedOutboundNames[adapter.Id] = adapter.Name;
        }
    }

    private bool RequiresGeneratedConfigRefresh(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> currentAdapters)
    {
        var requiredInterfaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRequiredInterfaces(settings.DefaultPolicy);
        foreach (var policy in settings.Policies.Where(value => value.Enabled))
        {
            AddRequiredInterfaces(policy);
        }

        foreach (var adapter in currentAdapters)
        {
            if (_generatedOutboundNames.TryGetValue(adapter.Id, out var previousName) &&
                !string.Equals(previousName, adapter.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (adapter.IsUserAllowed && requiredInterfaceIds.Contains(adapter.Id) &&
                !_generatedOutboundNames.ContainsKey(adapter.Id))
            {
                return true;
            }
        }

        return false;

        void AddRequiredInterfaces(RoutingPolicy policy)
        {
            if (!string.IsNullOrWhiteSpace(policy.PrimaryInterfaceId))
            {
                requiredInterfaceIds.Add(policy.PrimaryInterfaceId);
            }

            if (policy.FailoverEnabled && !string.IsNullOrWhiteSpace(policy.FallbackInterfaceId))
            {
                requiredInterfaceIds.Add(policy.FallbackInterfaceId);
            }
        }
    }

    private async Task<PipeResponse> RestartCoreAsync(CancellationToken cancellationToken)
    {
        await core.RestartAsync(cancellationToken);
        return new PipeResponse { Success = true };
    }

    private async Task<PipeResponse> ValidateConfigAsync(CancellationToken cancellationToken)
    {
        var error = await core.ValidateConfigurationAsync(_settings, await GetAdaptersAsync(cancellationToken), cancellationToken);
        return error is null ? new PipeResponse { Success = true } : Fail(error);
    }

    private async Task<PipeResponse> TestInterfaceAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var id = GetRequiredString(payload, "interfaceId");
        var family = Enum.Parse<AddressFamilyKind>(GetRequiredString(payload, "family"), ignoreCase: true);
        var selectedAdapter = health.Adapters.FirstOrDefault(value => value.Id == id);
        if (selectedAdapter is null) return Fail("Interface is no longer available.");
        if (!selectedAdapter.IsUserAllowed) return Fail("Allow this interface before testing it.");
        var before = health.GetLastProbeTime(id, family);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (health.GetLastProbeTime(id, family) is { } last && (before is null || last > before))
                completed.TrySetResult(true);
        }
        health.Changed += OnChanged;
        try
        {
            health.RequestProbe(id, family);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { await completed.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail("The interface probe did not complete within 30 seconds.");
            }
        }
        finally { health.Changed -= OnChanged; }
        var adapter = health.Adapters.FirstOrDefault(value => value.Id == id);
        var latency = family == AddressFamilyKind.IPv4 ? adapter?.IPv4Latency : adapter?.IPv6Latency;
        return Ok(new InterfaceTestResult
        {
            InterfaceId = id, Family = family.ToString(), Health = health.GetState(id, family).ToString(),
            LatencyMs = latency?.TotalMilliseconds
        }, ServiceJsonContext.Default.InterfaceTestResult);
    }

    private async Task<PipeResponse> ExportDiagnosticsAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        var redactIps = payload is { } options &&
            (options.TryGetProperty("redactIpAddresses", out var redact) || options.TryGetProperty("RedactIpAddresses", out redact)) &&
            redact.ValueKind == JsonValueKind.True;
        var fileName = $"EasyBalance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
        await WriteZipJsonAsync(zip, "metadata.json", new DiagnosticMetadata { SingBoxVersion = core.Snapshot.Version }, ServiceJsonContext.Default.DiagnosticMetadata, cancellationToken);
        await WriteZipJsonAsync(zip, "status.json", GetStatus(), ServiceJsonContext.Default.RuntimeStatus, cancellationToken);
        if (!redactIps)
            await WriteZipJsonAsync(zip, "settings.json", _settings, EasyBalanceJsonContext.Default.AppSettings, cancellationToken);
        var adapterSnapshot = (await GetAdaptersAsync(cancellationToken)).ToList();
        if (redactIps)
        {
            adapterSnapshot = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(adapterSnapshot, EasyBalanceJsonContext.Default.ListNetworkAdapterInfo),
                EasyBalanceJsonContext.Default.ListNetworkAdapterInfo)!;
            foreach (var adapter in adapterSnapshot)
            {
                adapter.IPv4Addresses = ["[redacted]"];
                adapter.IPv6Addresses = ["[redacted]"];
                adapter.IPv4Gateways = ["[redacted]"];
                adapter.IPv6Gateways = ["[redacted]"];
                adapter.DnsServers = ["[redacted]"];
            }
        }
        await WriteZipJsonAsync(zip, "adapters.json", adapterSnapshot, EasyBalanceJsonContext.Default.ListNetworkAdapterInfo, cancellationToken);
        await WriteZipJsonAsync(zip, "diagnostics.json", GetDiagnostics(), ServiceJsonContext.Default.RuntimeDiagnostics, cancellationToken);
        if (!redactIps)
            await WriteZipJsonAsync(zip, "logs.json", GetLogs(), ServiceJsonContext.Default.ListRuntimeLogEntry, cancellationToken);
        var redactedConfig = await ReadRedactedConfigAsync(cancellationToken);
        if (redactedConfig.Length > 0 && !redactIps)
        {
            var entry = zip.CreateEntry("sing-box.redacted.json");
            await using var stream = new StreamWriter(entry.Open());
            await stream.WriteAsync(redactedConfig.AsMemory(), cancellationToken);
        }
        }
        return Ok(new DiagnosticsBlobResult
        {
            FileName = fileName,
            DataBase64 = Convert.ToBase64String(file.ToArray())
        }, ServiceJsonContext.Default.DiagnosticsBlobResult);
    }

    private async Task<string> ReadRedactedConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(GeneratedConfigPath)) return string.Empty;
        var node = JsonNode.Parse(await File.ReadAllTextAsync(GeneratedConfigPath, cancellationToken));
        if (node?["experimental"]?["clash_api"] is JsonObject api)
            api["secret"] = "[redacted]";
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? string.Empty;
    }

    private static async Task WriteZipJsonAsync<T>(ZipArchive zip, string name, T value,
        JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken);
    }

    private static List<ProcessSnapshot> GetProcesses()
    {
        var records = new Dictionary<string, ProcessSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { path = null; }
                    var name = process.ProcessName;
                    var key = path ?? name;
                    if (!records.TryGetValue(key, out var item))
                        records[key] = item = new ProcessSnapshot { ProcessName = name, ExecutablePath = path };
                    item.Pids.Add(process.Id);
                }
                catch (InvalidOperationException) { /* Process exited during snapshot. */ }
            }
        }
        return records.Values.OrderBy(value => value.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void EnsureDataDirectory()
    {
        Directory.CreateDirectory(_dataDirectory);
        var currentIdentity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The service identity could not be determined.");
        if ((File.GetAttributes(_dataDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("EasyBalance data directory cannot be a reparse point.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentIdentity);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            currentIdentity
        }.OfType<SecurityIdentifier>().Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(_dataDirectory).SetAccessControl(security);
        if (File.Exists(SettingsPath))
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.SetOwner(currentIdentity);
            foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                         .OfType<FileSystemAccessRule>())
                fileSecurity.AddAccessRule(new FileSystemAccessRule(rule.IdentityReference, FileSystemRights.FullControl,
                    AccessControlType.Allow));
            new FileInfo(SettingsPath).SetAccessControl(fileSecurity);
        }
    }

    private void Log(string level, string source, string message)
    {
        lock (_logs)
        {
            _logs.Enqueue(new RuntimeLogEntry { Timestamp = DateTimeOffset.UtcNow, Level = level, Source = source, Message = message });
            while (_logs.Count > 500) _logs.Dequeue();
        }
    }

    private List<RuntimeLogEntry> GetLogs()
    {
        lock (_logs) return _logs.ToList();
    }

    private AppSettings CloneSettings() => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(_settings, EasyBalanceJsonContext.Default.AppSettings),
        EasyBalanceJsonContext.Default.AppSettings)!;

    private static T Read<T>(JsonElement? payload, JsonTypeInfo<T> typeInfo) =>
        payload is { } value ? value.Deserialize(typeInfo) ?? throw new JsonException("Empty payload.")
            : throw new JsonException("Missing payload.");

    private static string GetRequiredString(JsonElement? payload, string key) =>
        payload is { } value && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new JsonException($"Missing {key}.")
            : throw new JsonException($"Missing {key}.");

    private static bool GetRequiredBool(JsonElement? payload, string key) =>
        payload is { } value && value.TryGetProperty(key, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : throw new JsonException($"Missing {key}.");

    private static PipeResponse Ok<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new() { Success = true, Payload = JsonSerializer.SerializeToElement(value, typeInfo) };

    private static PipeResponse Fail(string error) => new() { Error = error };
}
