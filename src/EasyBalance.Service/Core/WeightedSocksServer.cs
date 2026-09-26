using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace EasyBalance.Service.Core;

/// <summary>One physical egress and its target share of observed proxy traffic.</summary>
public sealed record WeightedSocksEgressConfig(string InterfaceId, string Name, int WeightPercent);

/// <summary>The loopback endpoint and in-memory credentials sing-box uses for its SOCKS outbound.</summary>
public sealed record WeightedSocksEndpoint(IPAddress Address, int Port, string Username, string Password);

public sealed record WeightedSocksServerSnapshot(
    int? Port,
    IReadOnlyList<WeightedSocksEgressSnapshot> Egresses,
    IReadOnlyList<WeightedSocksConnectionSnapshot> Connections,
    string? LastError);

public sealed record WeightedSocksEgressSnapshot(
    string InterfaceId,
    string Name,
    int WeightPercent,
    long UploadBytes,
    long DownloadBytes,
    int ActiveConnections,
    bool Available,
    string? Error);

/// <summary>
/// A SOCKS proxy flow. SourceIp/SourcePort identify sing-box's loopback-side socket and can be
/// correlated with other connection views when their source tuple matches; callers should leave
/// the correlation unknown when it does not.
/// </summary>
public sealed record WeightedSocksConnectionSnapshot(
    string Id,
    string Protocol,
    string SourceIp,
    string SourcePort,
    string DestinationIp,
    int DestinationPort,
    string InterfaceId,
    string InterfaceName,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset StartedAt);

/// <summary>
/// A loopback-only authenticated SOCKS5 proxy for routing sing-box's default outbound across two
/// Windows network interfaces. New flows are assigned to the eligible egress with the lowest
/// observed-byte-to-target-weight ratio. Each flow stays on its selected interface for its lifetime.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WeightedSocksServer : IAsyncDisposable
{
    private const int RequiredEgressCount = 2;
    private const int MaximumUdpFlowsPerAssociation = 256;
    private const int MaximumTrackedClients = 512;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveConnection> _activeConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;
    private WeightedSocksEndpoint? _endpoint;
    private EgressRuntime[] _egresses = [];
    private string? _lastError;
    private long _clientTaskId;
    private long _connectionId;
    private int _tieBreaker;
    private bool _disposed;

    /// <summary>Raised for unsupported protocol details and network failures useful to the debug UI.</summary>
    public event Action<string>? DiagnosticReceived;

    /// <summary>Returns the current loopback listener and its credentials for in-process configuration.</summary>
    public WeightedSocksEndpoint? Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _endpoint;
            }
        }
    }

    public WeightedSocksServerSnapshot Snapshot
    {
        get
        {
            WeightedSocksEndpoint? endpoint;
            EgressRuntime[] egresses;
            string? lastError;
            lock (_gate)
            {
                endpoint = _endpoint;
                egresses = _egresses.ToArray();
                lastError = _lastError;
            }

            var connections = _activeConnections.Values
                .Select(connection => connection.ToSnapshot())
                .OrderByDescending(connection => connection.StartedAt)
                .ToArray();
            var egressSnapshots = egresses
                .Select(egress => egress.ToSnapshot())
                .ToArray();
            return new WeightedSocksServerSnapshot(endpoint?.Port, egressSnapshots, connections, lastError);
        }
    }

    /// <summary>
    /// Starts the listener on the first call. Later calls update weights and interface bindings
    /// without changing the listener, its credentials, or already established flows.
    /// Exactly two configured egresses are required. Both weights may be zero to suspend new flows.
    /// </summary>
    public async Task<WeightedSocksEndpoint> StartAsync(
        IReadOnlyCollection<WeightedSocksEgressConfig> egresses,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("WeightedSocksServer uses Windows interface-bound sockets.");
            }

            var normalized = ValidateAndNormalize(egresses);
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            lock (_gate)
            {
                ThrowIfDisposed();
                ReplaceEgressesUnderLock(normalized, interfaces);
                if (_listener is null)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start(512);
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var username = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    _listener = listener;
                    _endpoint = new WeightedSocksEndpoint(IPAddress.Loopback, port, username, password);
                    var lifetime = new CancellationTokenSource();
                    _lifetime = lifetime;
                    _acceptTask = Task.Run(() => AcceptLoopAsync(listener, lifetime.Token));
                }

                return _endpoint!;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Updates egress weights and bindings while preserving the listener and active flows.</summary>
    public void UpdateEgresses(IReadOnlyCollection<WeightedSocksEgressConfig> egresses)
    {
        ArgumentNullException.ThrowIfNull(egresses);
        var normalized = ValidateAndNormalize(egresses);
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_listener is null)
            {
                throw new InvalidOperationException("Start the weighted SOCKS server before updating its egresses.");
            }

            ReplaceEgressesUnderLock(normalized, interfaces);
        }
    }

    /// <summary>
    /// Stops the listener and its active flows but preserves byte counters and permits a later
    /// StartAsync call, which will create a fresh listener port and fresh credentials.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            await StopUnderLifecycleGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Changes just the target shares; values must sum to 100 or both be zero.</summary>
    public void UpdateWeights(IReadOnlyDictionary<string, int> weightsByInterfaceId)
    {
        ArgumentNullException.ThrowIfNull(weightsByInterfaceId);
        WeightedSocksEgressConfig[] current;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_listener is null)
            {
                throw new InvalidOperationException("Start the weighted SOCKS server before updating weights.");
            }

            current = _egresses.Select(egress =>
            {
                var weight = weightsByInterfaceId.FirstOrDefault(pair => InterfaceIdsEqual(pair.Key, egress.InterfaceId));
                if (weight.Key is null)
                {
                    throw new ArgumentException($"No weight was supplied for interface {egress.InterfaceId}.", nameof(weightsByInterfaceId));
                }

                return new WeightedSocksEgressConfig(egress.InterfaceId, egress.Name, weight.Value);
            }).ToArray();
        }

        UpdateEgresses(current);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await StopUnderLifecycleGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopUnderLifecycleGateAsync()
    {
        Task? acceptTask;
        CancellationTokenSource? lifetime;
        TcpListener? listener;
        lock (_gate)
        {
            listener = _listener;
            if (listener is null)
            {
                return;
            }

            lifetime = _lifetime;
            acceptTask = _acceptTask;
            _listener = null;
            _lifetime = null;
            _acceptTask = null;
            _endpoint = null;
        }

        lifetime?.Cancel();
        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }

        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        var clientTasks = _clientTasks.Values.ToArray();
        if (clientTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(clientTasks).ConfigureAwait(false);
            }
            catch
            {
                // Individual handlers report and contain their own failures.
            }
        }

        lifetime?.Dispose();
    }

    private static WeightedSocksEgressConfig[] ValidateAndNormalize(IReadOnlyCollection<WeightedSocksEgressConfig> egresses)
    {
        ArgumentNullException.ThrowIfNull(egresses);
        if (egresses.Count != RequiredEgressCount)
        {
            throw new ArgumentException("Exactly two egress interfaces must be configured.", nameof(egresses));
        }

        var normalized = egresses.Select(egress =>
        {
            if (egress is null || string.IsNullOrWhiteSpace(egress.InterfaceId) || string.IsNullOrWhiteSpace(egress.Name))
            {
                throw new ArgumentException("Each egress must have an interface ID and name.", nameof(egresses));
            }

            if (egress.WeightPercent is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(egresses), "Each egress weight must be between 0 and 100.");
            }

            var id = Guid.TryParse(egress.InterfaceId, out var guid)
                ? guid.ToString("D")
                : egress.InterfaceId.Trim();
            return new WeightedSocksEgressConfig(id, egress.Name.Trim(), egress.WeightPercent);
        }).ToArray();

        var totalWeight = normalized.Sum(egress => egress.WeightPercent);
        if (totalWeight is not 0 and not 100)
        {
            throw new ArgumentException("The two egress weights must sum to 100 or both be zero to suspend new flows.", nameof(egresses));
        }

        if (InterfaceIdsEqual(normalized[0].InterfaceId, normalized[1].InterfaceId))
        {
            throw new ArgumentException("The two egresses must refer to different network interfaces.", nameof(egresses));
        }

        return normalized;
    }

    private void ReplaceEgressesUnderLock(WeightedSocksEgressConfig[] configs, NetworkInterface[] interfaces)
    {
        var prior = _egresses.ToDictionary(egress => egress.InterfaceId, StringComparer.OrdinalIgnoreCase);
        var next = new EgressRuntime[configs.Length];
        for (var index = 0; index < configs.Length; index++)
        {
            var config = configs[index];
            var binding = CreateBinding(config, interfaces);
            if (prior.TryGetValue(config.InterfaceId, out var existing))
            {
                existing.Update(config, binding);
                next[index] = existing;
            }
            else
            {
                next[index] = new EgressRuntime(config, binding);
            }
        }

        _egresses = next;
    }

    private static EgressBinding CreateBinding(WeightedSocksEgressConfig config, NetworkInterface[] interfaces)
    {
        var networkInterface = interfaces.FirstOrDefault(candidate => InterfaceIdsEqual(candidate.Id, config.InterfaceId));
        if (networkInterface is null)
        {
            return EgressBinding.Unavailable(config.Name, $"Network interface {config.InterfaceId} is not present.");
        }

        if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return EgressBinding.Unavailable(config.Name, "Loopback cannot be used as a physical egress.");
        }

        try
        {
            var properties = networkInterface.GetIPProperties();
            var ipv4Index = properties.GetIPv4Properties()?.Index ?? 0;
            var ipv6Index = properties.GetIPv6Properties()?.Index ?? 0;
            var ipv4Address = properties.UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                           !IPAddress.IsLoopback(address) && !IsUnspecified(address));
            var ipv6Address = properties.UnicastAddresses
                .Select(unicast => unicast.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                           !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !IsUnspecified(address));
            var isUp = networkInterface.OperationalStatus == OperationalStatus.Up;
            string? error = null;
            if (!isUp)
            {
                error = $"Network interface {config.Name} is {networkInterface.OperationalStatus}.";
            }
            else if ((ipv4Address is null || ipv4Index == 0) && (ipv6Address is null || ipv6Index == 0))
            {
                error = $"Network interface {config.Name} has no usable IPv4 or IPv6 address.";
            }

            return new EgressBinding(config.Name, ipv4Address, ipv4Index, ipv6Address, ipv6Index, isUp, error);
        }
        catch (NetworkInformationException exception)
        {
            return EgressBinding.Unavailable(config.Name, $"Could not inspect network interface {config.Name}: {exception.Message}");
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ReportDiagnostic($"SOCKS listener accept failed: {exception.Message}");
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (_clientTasks.Count >= MaximumTrackedClients)
            {
                client.Dispose();
                ReportDiagnostic("SOCKS client limit reached; the new client was closed.");
                continue;
            }

            var taskId = Interlocked.Increment(ref _clientTaskId);
            var task = HandleClientAsync(client, cancellationToken);
            _clientTasks[taskId] = task;
            _ = task.ContinueWith(
                completed =>
                {
                    _clientTasks.TryRemove(taskId, out _);
                    _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            handshakeTimeout.CancelAfter(ConnectTimeout);
            try
            {
                var clientEndpoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (clientEndpoint is null || !IPAddress.IsLoopback(clientEndpoint.Address))
                {
                    client.Dispose();
                    return;
                }

                var endpoint = Endpoint;
                if (endpoint is null)
                {
                    return;
                }

                var stream = client.GetStream();
                if (!await AuthenticateAsync(stream, endpoint, handshakeTimeout.Token).ConfigureAwait(false))
                {
                    return;
                }

                var requestHeader = new byte[4];
                await ReadExactlyAsync(stream, requestHeader, handshakeTimeout.Token).ConfigureAwait(false);
                if (requestHeader[0] != 5 || requestHeader[2] != 0)
                {
                    await WriteReplyAsync(stream, 0x01, handshakeTimeout.Token).ConfigureAwait(false);
                    return;
                }

                var command = requestHeader[1];
                if (command is not 0x01 and not 0x03)
                {
                    await WriteReplyAsync(stream, 0x07, handshakeTimeout.Token).ConfigureAwait(false);
                    return;
                }

                var target = await ReadTargetAsync(stream, requestHeader[3], command == 0x03, handshakeTimeout.Token).ConfigureAwait(false);
                if (target is null)
                {
                    await WriteReplyAsync(stream, 0x08, handshakeTimeout.Token).ConfigureAwait(false);
                    return;
                }

                handshakeTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (command == 0x01)
                {
                    await HandleConnectAsync(client, stream, clientEndpoint, target, serverToken).ConfigureAwait(false);
                }
                else
                {
                    await HandleUdpAssociateAsync(client, stream, clientEndpoint, target, serverToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                ReportDiagnostic("SOCKS client handshake timed out.");
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (Exception exception)
            {
                ReportDiagnostic($"SOCKS client handling failed: {exception.Message}");
            }
        }
    }

    private static async Task<bool> AuthenticateAsync(Stream stream, WeightedSocksEndpoint endpoint, CancellationToken cancellationToken)
    {
        var greetingHeader = new byte[2];
        await ReadExactlyAsync(stream, greetingHeader, cancellationToken).ConfigureAwait(false);
        if (greetingHeader[0] != 5)
        {
            return false;
        }

        var methodCount = greetingHeader[1];
        var methods = new byte[methodCount];
        await ReadExactlyAsync(stream, methods, cancellationToken).ConfigureAwait(false);
        if (!methods.Contains((byte)0x02))
        {
            await stream.WriteAsync(new byte[] { 5, 0xFF }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await stream.WriteAsync(new byte[] { 5, 0x02 }, cancellationToken).ConfigureAwait(false);
        var authHeader = new byte[2];
        await ReadExactlyAsync(stream, authHeader, cancellationToken).ConfigureAwait(false);
        if (authHeader[0] != 1)
        {
            await stream.WriteAsync(new byte[] { 1, 1 }, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var usernameBytes = new byte[authHeader[1]];
        await ReadExactlyAsync(stream, usernameBytes, cancellationToken).ConfigureAwait(false);
        var passwordLength = new byte[1];
        await ReadExactlyAsync(stream, passwordLength, cancellationToken).ConfigureAwait(false);
        var passwordBytes = new byte[passwordLength[0]];
        await ReadExactlyAsync(stream, passwordBytes, cancellationToken).ConfigureAwait(false);
        var expectedUsername = Encoding.ASCII.GetBytes(endpoint.Username);
        var expectedPassword = Encoding.ASCII.GetBytes(endpoint.Password);
        var validUsername = usernameBytes.Length == expectedUsername.Length &&
                            CryptographicOperations.FixedTimeEquals(usernameBytes, expectedUsername);
        var validPassword = passwordBytes.Length == expectedPassword.Length &&
                            CryptographicOperations.FixedTimeEquals(passwordBytes, expectedPassword);
        var authenticated = validUsername & validPassword;
        await stream.WriteAsync(new byte[] { 1, authenticated ? (byte)0 : (byte)1 }, cancellationToken).ConfigureAwait(false);
        return authenticated;
    }

    private async Task HandleConnectAsync(
        TcpClient client,
        Stream clientStream,
        IPEndPoint clientEndpoint,
        SocksTarget target,
        CancellationToken cancellationToken)
    {
        if (GetOrderedEgresses().Length == 0)
        {
            ReportDiagnostic("SOCKS CONNECT refused because both configured egress weights are zero or unavailable.");
            await WriteReplyAsync(clientStream, 0x03, cancellationToken).ConfigureAwait(false);
            return;
        }

        var destinations = await ResolveTargetAsync(target.HostOrAddress, cancellationToken).ConfigureAwait(false);
        if (destinations.Length == 0)
        {
            await WriteReplyAsync(clientStream, 0x04, cancellationToken).ConfigureAwait(false);
            return;
        }

        Socket? remoteSocket = null;
        EgressRuntime? selectedEgress = null;
        IPEndPoint? selectedDestination = null;
        byte replyCode = 0x04;
        foreach (var egress in GetOrderedEgresses())
        {
            foreach (var destination in destinations.Where(address => egress.Supports(address.AddressFamily)))
            {
                var binding = egress.Binding;
                var source = binding.GetSource(destination.AddressFamily);
                var index = binding.GetIndex(destination.AddressFamily);
                if (source is null || index == 0)
                {
                    continue;
                }

                var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    BindToEgress(socket, source, index);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(new IPEndPoint(destination, target.Port), timeout.Token).ConfigureAwait(false);
                    remoteSocket = socket;
                    selectedEgress = egress;
                    selectedDestination = new IPEndPoint(destination, target.Port);
                    replyCode = 0;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    replyCode = 0x06;
                    socket.Dispose();
                }
                catch (SocketException exception)
                {
                    replyCode = MapSocketError(exception.SocketErrorCode);
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }

                if (remoteSocket is not null)
                {
                    break;
                }
            }

            if (remoteSocket is not null)
            {
                break;
            }
        }

        if (remoteSocket is null || selectedEgress is null || selectedDestination is null)
        {
            if (GetOrderedEgresses().Length == 0)
            {
                ReportDiagnostic("SOCKS CONNECT failed because neither configured egress is currently available for the destination address family.");
                replyCode = 0x03;
            }
            else
            {
                ReportDiagnostic($"SOCKS CONNECT could not reach {target.HostOrAddress}:{target.Port} on any eligible egress.");
            }

            await WriteReplyAsync(clientStream, replyCode, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (remoteSocket)
        {
            await WriteReplyAsync(clientStream, 0x00, cancellationToken).ConfigureAwait(false);
            var active = new ActiveConnection(
                Interlocked.Increment(ref _connectionId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "TCP",
                clientEndpoint,
                selectedDestination,
                selectedEgress,
                DateTimeOffset.UtcNow);
            RegisterConnection(active);
            try
            {
                using var remoteStream = new NetworkStream(remoteSocket, ownsSocket: false);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var upload = CopyLoopAsync(clientStream, remoteStream, remoteSocket, active.AddUpload, lifetime.Token);
                var download = CopyLoopAsync(remoteStream, clientStream, client.Client, active.AddDownload, lifetime.Token);
                var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
                if (first.IsFaulted || first.IsCanceled)
                {
                    lifetime.Cancel();
                    TryShutdown(remoteSocket, SocketShutdown.Both);
                    TryShutdown(client.Client, SocketShutdown.Both);
                }

                try
                {
                    await Task.WhenAll(upload, download).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
            finally
            {
                UnregisterConnection(active);
            }
        }
    }

    private async Task HandleUdpAssociateAsync(
        TcpClient client,
        Stream controlStream,
        IPEndPoint controlClient,
        SocksTarget target,
        CancellationToken serverToken)
    {
        if (target.ResolvedAddress is { } requestedAddress &&
            !IsUnspecified(requestedAddress) &&
            !requestedAddress.Equals(controlClient.Address))
        {
            await WriteReplyAsync(controlStream, 0x02, serverToken).ConfigureAwait(false);
            ReportDiagnostic("SOCKS UDP ASSOCIATE was rejected because the requested client address did not match its loopback control connection.");
            return;
        }

        using var relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var relayEndpoint = (IPEndPoint)relay.LocalEndPoint!;
        await WriteReplyAsync(controlStream, 0x00, serverToken, relayEndpoint).ConfigureAwait(false);

        using var associationLifetime = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var association = new UdpAssociation(controlClient.Address, target.Port);
        var flows = new ConcurrentDictionary<string, UdpFlow>(StringComparer.Ordinal);
        var receiveTask = ReceiveUdpClientDatagramsAsync(relay, association, flows, associationLifetime.Token);
        var controlTask = MonitorControlConnectionAsync(controlStream, associationLifetime.Token);
        await Task.WhenAny(receiveTask, controlTask).ConfigureAwait(false);
        associationLifetime.Cancel();
        TryShutdown(client.Client, SocketShutdown.Both);

        try
        {
            await Task.WhenAll(receiveTask, controlTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (associationLifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (associationLifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (associationLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportDiagnostic($"SOCKS UDP association stopped: {exception.Message}");
        }

        var udpFlows = flows.Values.ToArray();
        foreach (var flow in udpFlows)
        {
            flow.Socket.Dispose();
        }

        var receiveTasks = udpFlows.Select(flow => flow.ReceiveTask).Where(task => task is not null).Cast<Task>().ToArray();
        if (receiveTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(receiveTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (associationLifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (associationLifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (associationLifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReportDiagnostic($"SOCKS UDP flow cleanup reported: {exception.Message}");
            }
        }

        foreach (var flow in udpFlows)
        {
            UnregisterConnection(flow.Connection);
            flow.Dispose();
        }
    }

    private async Task ReceiveUdpClientDatagramsAsync(
        Socket relay,
        UdpAssociation association,
        ConcurrentDictionary<string, UdpFlow> flows,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65535];
        while (!cancellationToken.IsCancellationRequested)
        {
            EndPoint anyClient = new IPEndPoint(IPAddress.Any, 0);
            var received = await relay.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, anyClient, cancellationToken).ConfigureAwait(false);
            if (received.RemoteEndPoint is not IPEndPoint clientEndpoint ||
                !association.TryAcceptClientEndpoint(clientEndpoint))
            {
                ReportDiagnostic("SOCKS UDP datagram was dropped because it did not come from the loopback SOCKS client endpoint.");
                continue;
            }

            if (!TryParseUdpPacket(buffer.AsSpan(0, received.ReceivedBytes), out var target, out var payloadOffset))
            {
                ReportDiagnostic("SOCKS UDP datagram was dropped because its SOCKS UDP header was malformed.");
                continue;
            }

            if (target.Fragment != 0)
            {
                ReportDiagnostic("Fragmented SOCKS UDP datagrams are unsupported and were dropped.");
                continue;
            }

            var payload = buffer.AsMemory(payloadOffset, received.ReceivedBytes - payloadOffset);
            var key = BuildUdpFlowKey(target.HostOrAddress, target.Port);
            if (!flows.TryGetValue(key, out var flow))
            {
                if (GetOrderedEgresses().Length == 0)
                {
                    ReportDiagnostic("SOCKS UDP datagram was refused because both configured egress weights are zero or unavailable.");
                    continue;
                }

                var destinations = await ResolveTargetAsync(target.HostOrAddress, cancellationToken).ConfigureAwait(false);
                if (destinations.Length == 0)
                {
                    ReportDiagnostic($"SOCKS UDP destination {target.HostOrAddress}:{target.Port} could not be resolved.");
                    continue;
                }

                if (flows.Count >= MaximumUdpFlowsPerAssociation)
                {
                    ReportDiagnostic("SOCKS UDP association flow limit reached; the new destination was dropped.");
                    continue;
                }

                flow = await CreateUdpFlowAsync(destinations, target.Port, clientEndpoint, relay, association, cancellationToken).ConfigureAwait(false);
                if (flow is null)
                {
                    ReportDiagnostic($"No configured egress could open SOCKS UDP destination {target.HostOrAddress}:{target.Port}.");
                    continue;
                }

                flows[key] = flow;
            }

            try
            {
                var sent = await flow.Socket.SendAsync(payload, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                flow.Connection.AddUpload(sent);
            }
            catch (SocketException exception)
            {
                ReportDiagnostic($"SOCKS UDP send failed for {flow.Connection.DestinationIp}:{flow.Connection.DestinationPort}: {exception.Message}");
            }
        }
    }

    private async Task<UdpFlow?> CreateUdpFlowAsync(
        IReadOnlyList<IPAddress> destinations,
        int port,
        IPEndPoint clientEndpoint,
        Socket relay,
        UdpAssociation association,
        CancellationToken cancellationToken)
    {
        foreach (var egress in GetOrderedEgresses())
        {
            foreach (var destination in destinations.Where(address => egress.Supports(address.AddressFamily)))
            {
                var binding = egress.Binding;
                var source = binding.GetSource(destination.AddressFamily);
                var index = binding.GetIndex(destination.AddressFamily);
                if (source is null || index == 0)
                {
                    continue;
                }

                var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    BindToEgress(socket, source, index);
                    await socket.ConnectAsync(new IPEndPoint(destination, port), cancellationToken).ConfigureAwait(false);
                    var destinationEndpoint = new IPEndPoint(destination, port);
                    var active = new ActiveConnection(
                        Interlocked.Increment(ref _connectionId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "UDP",
                        clientEndpoint,
                        destinationEndpoint,
                        egress,
                        DateTimeOffset.UtcNow);
                    RegisterConnection(active);
                    var flow = new UdpFlow(socket, active, relay, association);
                    flow.ReceiveTask = RelayUdpResponsesAsync(flow, cancellationToken);
                    return flow;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    socket.Dispose();
                    throw;
                }
                catch (SocketException exception)
                {
                    ReportDiagnostic($"SOCKS UDP egress {egress.Name} could not open {destination}:{port}: {exception.Message}");
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }

        return null;
    }

    private async Task RelayUdpResponsesAsync(UdpFlow flow, CancellationToken cancellationToken)
    {
        var buffer = new byte[65535];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await flow.Socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (!flow.Association.TryGetClientEndpoint(out var clientEndpoint))
                {
                    continue;
                }

                var remote = (IPEndPoint)flow.Socket.RemoteEndPoint!;
                var packet = MakeUdpPacket(remote, buffer.AsSpan(0, count));
                await flow.Relay.SendToAsync(packet, SocketFlags.None, clientEndpoint, cancellationToken).ConfigureAwait(false);
                flow.Connection.AddDownload(count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            ReportDiagnostic($"SOCKS UDP receive failed for {flow.Connection.DestinationIp}:{flow.Connection.DestinationPort}: {exception.Message}");
        }
    }

    private static async Task MonitorControlConnectionAsync(Stream controlStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (await controlStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    private async Task<IPAddress[]> ResolveTargetAsync(string hostOrAddress, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(hostOrAddress, out var address))
        {
            return IsRoutableTarget(address) ? [address] : [];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostOrAddress, cancellationToken).ConfigureAwait(false);
            return addresses
                .Where(IsRoutableTarget)
                .Distinct()
                .Take(32)
                .ToArray();
        }
        catch (SocketException exception)
        {
            ReportDiagnostic($"SOCKS destination DNS lookup failed for {hostOrAddress}: {exception.Message}");
            return [];
        }
    }

    private EgressRuntime[] GetOrderedEgresses()
    {
        EgressRuntime[] available;
        lock (_gate)
        {
            available = _egresses
                .Where(egress => egress.WeightPercent > 0 && egress.Binding.IsAvailable)
                .ToArray();
        }

        if (available.Length < 2)
        {
            return available.OrderBy(egress => egress.LoadScore).ToArray();
        }

        var first = (int)((uint)Interlocked.Increment(ref _tieBreaker) % (uint)available.Length);
        var tieRank = available
            .Select((egress, index) => (egress.InterfaceId, Rank: (index - first + available.Length) % available.Length))
            .ToDictionary(item => item.InterfaceId, item => item.Rank, StringComparer.OrdinalIgnoreCase);
        return available
            .OrderBy(egress => egress.LoadScore)
            .ThenBy(egress => tieRank[egress.InterfaceId])
            .ToArray();
    }

    private void RegisterConnection(ActiveConnection connection)
    {
        _activeConnections[connection.Id] = connection;
        connection.Egress.AddActiveConnection();
    }

    private void UnregisterConnection(ActiveConnection connection)
    {
        if (_activeConnections.TryRemove(connection.Id, out _))
        {
            connection.Egress.RemoveActiveConnection();
        }
    }

    private void ReportDiagnostic(string message)
    {
        lock (_gate)
        {
            _lastError = message.Length <= 512 ? message : message[..512];
        }

        try
        {
            DiagnosticReceived?.Invoke(message);
        }
        catch
        {
            // Diagnostics must not interrupt traffic forwarding.
        }
    }

    private static bool InterfaceIdsEqual(string left, string right)
    {
        return Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid)
            ? leftGuid == rightGuid
            : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static bool IsRoutableTarget(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || IsUnspecified(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var firstOctet = address.GetAddressBytes()[0];
            return firstOctet is not (>= 224 and <= 239) && !address.Equals(IPAddress.Broadcast);
        }

        return true;
    }

    private static void BindToEgress(Socket socket, IPAddress sourceAddress, int interfaceIndex)
    {
        var isIpv4 = sourceAddress.AddressFamily == AddressFamily.InterNetwork;
        // Windows IP_UNICAST_IF takes network byte order; IPV6_UNICAST_IF takes host byte order.
        socket.SetSocketOption(
            isIpv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
            (SocketOptionName)31,
            isIpv4 ? IPAddress.HostToNetworkOrder(interfaceIndex) : interfaceIndex);
        socket.Bind(new IPEndPoint(sourceAddress, 0));
    }

    private static void TryShutdown(Socket socket, SocketShutdown how)
    {
        try
        {
            socket.Shutdown(how);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static byte MapSocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => 0x05,
        SocketError.NetworkUnreachable => 0x03,
        SocketError.HostUnreachable => 0x04,
        SocketError.TimedOut => 0x06,
        SocketError.AddressFamilyNotSupported => 0x08,
        SocketError.AddressNotAvailable => 0x03,
        _ => 0x01
    };

    private static async Task CopyLoopAsync(
        Stream source,
        Stream destination,
        Socket destinationSocket,
        Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                TryShutdown(destinationSocket, SocketShutdown.Send);
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes(read);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static async Task WriteReplyAsync(
        Stream stream,
        byte replyCode,
        CancellationToken cancellationToken,
        IPEndPoint? relayEndpoint = null)
    {
        var endpoint = relayEndpoint ?? new IPEndPoint(IPAddress.Any, 0);
        var addressBytes = endpoint.Address.GetAddressBytes();
        var addressType = endpoint.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        var reply = new byte[4 + addressBytes.Length + 2];
        reply[0] = 5;
        reply[1] = replyCode;
        reply[2] = 0;
        reply[3] = addressType;
        addressBytes.CopyTo(reply, 4);
        var portOffset = 4 + addressBytes.Length;
        reply[portOffset] = (byte)(endpoint.Port >> 8);
        reply[portOffset + 1] = (byte)endpoint.Port;
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SocksTarget?> ReadTargetAsync(
        Stream stream,
        byte addressType,
        bool allowLoopbackOrUnspecified,
        CancellationToken cancellationToken)
    {
        string host;
        IPAddress? parsedAddress = null;
        switch (addressType)
        {
            case 0x01:
            {
                var bytes = new byte[4];
                await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
                parsedAddress = new IPAddress(bytes);
                host = parsedAddress.ToString();
                break;
            }
            case 0x04:
            {
                var bytes = new byte[16];
                await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
                parsedAddress = new IPAddress(bytes);
                host = parsedAddress.ToString();
                break;
            }
            case 0x03:
            {
                var lengthBuffer = new byte[1];
                await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
                if (lengthBuffer[0] == 0)
                {
                    return null;
                }

                var bytes = new byte[lengthBuffer[0]];
                await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
                if (bytes.Any(value => value is 0 or > 0x7F))
                {
                    return null;
                }

                host = Encoding.ASCII.GetString(bytes);
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                {
                    return null;
                }

                break;
            }
            default:
                return null;
        }

        var portBytes = new byte[2];
        await ReadExactlyAsync(stream, portBytes, cancellationToken).ConfigureAwait(false);
        var port = (portBytes[0] << 8) | portBytes[1];
        if (parsedAddress is not null &&
            !IsRoutableTarget(parsedAddress) &&
            !(allowLoopbackOrUnspecified && (IPAddress.IsLoopback(parsedAddress) || IsUnspecified(parsedAddress))))
        {
            return null;
        }

        return new SocksTarget(host, port, parsedAddress);
    }

    private static bool TryParseUdpPacket(ReadOnlySpan<byte> packet, out UdpTarget target, out int payloadOffset)
    {
        target = default;
        payloadOffset = 0;
        if (packet.Length < 4 || packet[0] != 0 || packet[1] != 0)
        {
            return false;
        }

        var fragment = packet[2];
        var addressType = packet[3];
        var offset = 4;
        string host;
        switch (addressType)
        {
            case 0x01:
                if (packet.Length < offset + 4)
                {
                    return false;
                }

                host = new IPAddress(packet.Slice(offset, 4)).ToString();
                offset += 4;
                break;
            case 0x04:
                if (packet.Length < offset + 16)
                {
                    return false;
                }

                host = new IPAddress(packet.Slice(offset, 16)).ToString();
                offset += 16;
                break;
            case 0x03:
                if (packet.Length < offset + 1)
                {
                    return false;
                }

                var nameLength = packet[offset++];
                if (nameLength == 0 || packet.Length < offset + nameLength || !IsAsciiDomain(packet.Slice(offset, nameLength)))
                {
                    return false;
                }

                host = Encoding.ASCII.GetString(packet.Slice(offset, nameLength));
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                {
                    return false;
                }

                offset += nameLength;
                break;
            default:
                return false;
        }

        if (packet.Length < offset + 2)
        {
            return false;
        }

        var port = (packet[offset] << 8) | packet[offset + 1];
        offset += 2;
        if (port == 0 || IPAddress.TryParse(host, out var address) && !IsRoutableTarget(address))
        {
            return false;
        }

        target = new UdpTarget(host, port, fragment);
        payloadOffset = offset;
        return true;
    }

    private static byte[] MakeUdpPacket(IPEndPoint source, ReadOnlySpan<byte> payload)
    {
        var address = source.Address.GetAddressBytes();
        var addressType = source.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
        var packet = new byte[4 + address.Length + 2 + payload.Length];
        packet[0] = 0;
        packet[1] = 0;
        packet[2] = 0;
        packet[3] = addressType;
        address.CopyTo(packet, 4);
        var portOffset = 4 + address.Length;
        packet[portOffset] = (byte)(source.Port >> 8);
        packet[portOffset + 1] = (byte)source.Port;
        payload.CopyTo(packet.AsSpan(portOffset + 2));
        return packet;
    }

    private static string BuildUdpFlowKey(string host, int port) =>
        $"{host.ToLowerInvariant()}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static void ThrowIfDisposed(bool disposed)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WeightedSocksServer));
        }
    }

    private void ThrowIfDisposed() => ThrowIfDisposed(_disposed);

    private static bool IsAsciiDomain(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is 0 or > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SocksTarget(string HostOrAddress, int Port, IPAddress? ResolvedAddress);
    private readonly record struct UdpTarget(string HostOrAddress, int Port, byte Fragment);

    private sealed class EgressBinding(
        string name,
        IPAddress? ipv4Address,
        int ipv4Index,
        IPAddress? ipv6Address,
        int ipv6Index,
        bool isUp,
        string? error)
    {
        public static EgressBinding Unavailable(string name, string error) => new(name, null, 0, null, 0, false, error);

        public string Name { get; } = name;
        public IPAddress? IPv4Address { get; } = ipv4Address;
        public int IPv4Index { get; } = ipv4Index;
        public IPAddress? IPv6Address { get; } = ipv6Address;
        public int IPv6Index { get; } = ipv6Index;
        public bool IsUp { get; } = isUp;
        public string? Error { get; } = error;
        public bool IsAvailable => IsUp && ((IPv4Address is not null && IPv4Index > 0) || (IPv6Address is not null && IPv6Index > 0));

        public IPAddress? GetSource(AddressFamily family) => family == AddressFamily.InterNetwork ? IPv4Address : family == AddressFamily.InterNetworkV6 ? IPv6Address : null;
        public int GetIndex(AddressFamily family) => family == AddressFamily.InterNetwork ? IPv4Index : family == AddressFamily.InterNetworkV6 ? IPv6Index : 0;
    }

    private sealed class EgressRuntime
    {
        private EgressBinding _binding;
        private string _name;
        private int _weightPercent;
        private long _uploadBytes;
        private long _downloadBytes;
        private int _activeConnections;

        public EgressRuntime(WeightedSocksEgressConfig config, EgressBinding binding)
        {
            InterfaceId = config.InterfaceId;
            _name = config.Name;
            _weightPercent = config.WeightPercent;
            _binding = binding;
        }

        public string InterfaceId { get; }
        public string Name => Volatile.Read(ref _name);
        public int WeightPercent => Volatile.Read(ref _weightPercent);
        public EgressBinding Binding => Volatile.Read(ref _binding);
        public long UploadBytes => Interlocked.Read(ref _uploadBytes);
        public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
        public int ActiveConnections => Volatile.Read(ref _activeConnections);
        public double LoadScore
        {
            get
            {
                var weight = WeightPercent;
                return weight <= 0 ? double.PositiveInfinity : ((double)UploadBytes + DownloadBytes) / weight;
            }
        }

        public bool Supports(AddressFamily family)
        {
            var binding = Binding;
            return binding.IsUp && binding.GetSource(family) is not null && binding.GetIndex(family) > 0;
        }

        public void Update(WeightedSocksEgressConfig config, EgressBinding binding)
        {
            Volatile.Write(ref _name, config.Name);
            Volatile.Write(ref _weightPercent, config.WeightPercent);
            Volatile.Write(ref _binding, binding);
        }

        public void AddUpload(long bytes) => Interlocked.Add(ref _uploadBytes, bytes);
        public void AddDownload(long bytes) => Interlocked.Add(ref _downloadBytes, bytes);
        public void AddActiveConnection() => Interlocked.Increment(ref _activeConnections);
        public void RemoveActiveConnection() => Interlocked.Decrement(ref _activeConnections);

        public WeightedSocksEgressSnapshot ToSnapshot() => new(
            InterfaceId,
            Name,
            WeightPercent,
            UploadBytes,
            DownloadBytes,
            ActiveConnections,
            Binding.IsAvailable,
            Binding.Error);
    }

    private sealed class ActiveConnection
    {
        private long _uploadBytes;
        private long _downloadBytes;

        public ActiveConnection(
            string id,
            string protocol,
            IPEndPoint source,
            IPEndPoint destination,
            EgressRuntime egress,
            DateTimeOffset startedAt)
        {
            Id = id;
            Protocol = protocol;
            SourceIp = source.Address.ToString();
            SourcePort = source.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DestinationIp = destination.Address.ToString();
            DestinationPort = destination.Port;
            Egress = egress;
            StartedAt = startedAt;
        }

        public string Id { get; }
        public string Protocol { get; }
        public string SourceIp { get; }
        public string SourcePort { get; }
        public string DestinationIp { get; }
        public int DestinationPort { get; }
        public EgressRuntime Egress { get; }
        public DateTimeOffset StartedAt { get; }
        public long UploadBytes => Interlocked.Read(ref _uploadBytes);
        public long DownloadBytes => Interlocked.Read(ref _downloadBytes);

        public void AddUpload(long bytes)
        {
            Interlocked.Add(ref _uploadBytes, bytes);
            Egress.AddUpload(bytes);
        }

        public void AddDownload(long bytes)
        {
            Interlocked.Add(ref _downloadBytes, bytes);
            Egress.AddDownload(bytes);
        }

        public WeightedSocksConnectionSnapshot ToSnapshot() => new(
            Id,
            Protocol,
            SourceIp,
            SourcePort,
            DestinationIp,
            DestinationPort,
            Egress.InterfaceId,
            Egress.Name,
            UploadBytes,
            DownloadBytes,
            StartedAt);
    }

    private sealed class UdpAssociation(IPAddress controlClientAddress, int expectedClientPort)
    {
        private readonly object _gate = new();
        private IPEndPoint? _clientEndpoint;

        public bool TryAcceptClientEndpoint(IPEndPoint endpoint)
        {
            if (!IPAddress.IsLoopback(endpoint.Address) || !endpoint.Address.Equals(controlClientAddress) ||
                expectedClientPort != 0 && endpoint.Port != expectedClientPort)
            {
                return false;
            }

            lock (_gate)
            {
                if (_clientEndpoint is null)
                {
                    _clientEndpoint = endpoint;
                    return true;
                }

                return _clientEndpoint.Address.Equals(endpoint.Address) && _clientEndpoint.Port == endpoint.Port;
            }
        }

        public bool TryGetClientEndpoint(out IPEndPoint endpoint)
        {
            lock (_gate)
            {
                if (_clientEndpoint is null)
                {
                    endpoint = null!;
                    return false;
                }

                endpoint = _clientEndpoint;
                return true;
            }
        }
    }

    private sealed class UdpFlow(Socket socket, ActiveConnection connection, Socket relay, UdpAssociation association) : IDisposable
    {
        public Socket Socket { get; } = socket;
        public ActiveConnection Connection { get; } = connection;
        public Socket Relay { get; } = relay;
        public UdpAssociation Association { get; } = association;
        public Task? ReceiveTask { get; set; }

        public void Dispose() => Socket.Dispose();
    }
}
