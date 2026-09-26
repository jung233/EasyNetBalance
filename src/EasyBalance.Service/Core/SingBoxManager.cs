using System.Diagnostics;
using System.Security.AccessControl;
using System.Security;
using System.Security.Principal;
using System.Text;
using EasyBalance.Shared;
using Microsoft.Extensions.Logging;

namespace EasyBalance.Service.Core;

public sealed class SingBoxManager : IAsyncDisposable
{
    private static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StableRunDuration = TimeSpan.FromMinutes(2);
    private const int FaultAfterFailuresInWindow = 6;
    private const int MaximumRecentLogs = 300;

    private readonly ILogger<SingBoxManager> _logger;
    private readonly SingBoxCapabilitiesDetector _capabilitiesDetector;
    private readonly SingBoxConfigGenerator _configGenerator;
    private readonly ISingBoxControlClient _controlClient;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private readonly Queue<SingBoxCoreLogEntry> _recentLogs = new();
    private readonly Queue<DateTimeOffset> _recentFailures = new();

    private Process? _process;
    private SingBoxGeneratedConfig? _generatedConfig;
    private SingBoxCapabilities? _capabilities;
    private AppSettings? _lastSettings;
    private NetworkAdapterInfo[] _lastAdapters = [];
    private string? _executablePath;
    private string? _lastError;
    private string? _version;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastStartedAt;
    private int? _lastExitCode;
    private bool _coreRunning;
    private bool _faulted;
    private bool _explicitlyStopped;
    private long _processGeneration;
    private bool _disposed;

    public SingBoxManager(
        ILogger<SingBoxManager> logger,
        SingBoxCapabilitiesDetector capabilitiesDetector,
        SingBoxConfigGenerator configGenerator,
        ISingBoxControlClient controlClient)
    {
        _logger = logger;
        _capabilitiesDetector = capabilitiesDetector;
        _configGenerator = configGenerator;
        _controlClient = controlClient;
    }

    public ISingBoxControlClient Control => _controlClient;

    public string GeneratedConfigPath => GetDefaultConfigPath();

    public SingBoxCoreSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                TimeSpan? uptime = _startedAt is { } start && _coreRunning
                    ? DateTimeOffset.UtcNow - start
                    : null;
                return new SingBoxCoreSnapshot(
                    _coreRunning,
                    _faulted,
                    _version,
                    uptime,
                    _lastError,
                    _lastExitCode,
                    _startedAt,
                    _lastStartedAt,
                    GetCurrentProcessId());
            }
        }
    }

    public event EventHandler<SingBoxCoreLogEntry>? LogReceived;

    public async Task StartAsync(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapters);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false })
            {
                return;
            }

            _explicitlyStopped = false;
            await ConfigureAndStartUnderGateAsync(settings, adapters, replaceActiveConfig: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFaulted(exception.Message);
            AddLog("Error", $"sing-box startup failed: {Sanitize(exception.Message, _generatedConfig?.ApiSecret)}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Validates a newly generated configuration using sing-box's actual `check` command.
    /// Returns null when valid and a user-displayable error otherwise. It never starts sing-box.
    /// </summary>
    public async Task<string?> ValidateConfigurationAsync(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapters);
        var temporaryPath = string.Empty;
        try
        {
            var executablePath = ResolveExecutablePath(settings);
            var capabilities = await _capabilitiesDetector.DetectAsync(executablePath, cancellationToken).ConfigureAwait(false);
            capabilities.EnsureCompatible();
            var generated = _configGenerator.Generate(settings, adapters, capabilities);
            EnsureSecureConfigDirectory();
            temporaryPath = GetStagingPath();
            await WriteSecureConfigAsync(temporaryPath, generated.Json, cancellationToken).ConfigureAwait(false);
            var result = await RunCheckAsync(executablePath, temporaryPath, generated.ApiSecret, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? null : BuildCheckError(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Sanitize(exception.Message, null);
        }
        finally
        {
            DeleteFileIfPresent(temporaryPath);
        }
    }

    /// <summary>
    /// Applies a settings or rule change as a configuration transaction: generate and validate
    /// a protected staging file, atomically promote it, then restart the core. On start failure,
    /// the previous file is restored and the previous running configuration is restarted.
    /// </summary>
    public async Task ApplyConfigurationAsync(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapters);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var hadRunningProcess = _process is { HasExited: false };
            var priorSettings = _lastSettings;
            var shouldRun = hadRunningProcess || (!_explicitlyStopped && priorSettings?.Enabled == true);
            var priorAdapters = _lastAdapters;
            var priorGenerated = _generatedConfig;
            var priorCapabilities = _capabilities;
            var priorExecutablePath = _executablePath;
            var transaction = await PrepareValidatedConfigurationAsync(settings, adapters, cancellationToken).ConfigureAwait(false);
            var activePath = GeneratedConfigPath;
            var backupPath = activePath + ".rollback-" + Guid.NewGuid().ToString("N");
            var promoted = false;
            var stagingPath = transaction.StagingPath;

            try
            {
                if (hadRunningProcess)
                {
                    await StopProcessUnderGateAsync(markExplicit: false, cancellationToken).ConfigureAwait(false);
                }

                PromoteConfiguration(stagingPath, activePath, backupPath);
                promoted = true;
                _generatedConfig = transaction.Generated;
                _capabilities = transaction.Capabilities;
                _executablePath = transaction.ExecutablePath;
                _lastSettings = settings;
                _lastAdapters = adapters.ToArray();

                if (shouldRun)
                {
                    await StartProcessUnderGateAsync(transaction.ExecutablePath, activePath, transaction.Generated, transaction.Capabilities, cancellationToken).ConfigureAwait(false);
                    await VerifyStartupSurvivalAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }

                DeleteFileIfPresent(backupPath);
                ClearRestartFailures();
                _explicitlyStopped = false;
            }
            catch (Exception applyException)
            {
                if (promoted)
                {
                    await StopProcessUnderGateAsync(markExplicit: false, CancellationToken.None).ConfigureAwait(false);
                    RestoreConfiguration(activePath, backupPath);
                }

                _generatedConfig = priorGenerated;
                _capabilities = priorCapabilities;
                _executablePath = priorExecutablePath;
                _lastSettings = priorSettings;
                _lastAdapters = priorAdapters;

                if (shouldRun && _process is null && priorGenerated is not null && priorCapabilities is not null && priorExecutablePath is not null)
                {
                    try
                    {
                        await StartProcessUnderGateAsync(
                            priorExecutablePath,
                            activePath,
                            priorGenerated,
                            priorCapabilities,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException)
                    {
                        SetFaulted($"Configuration apply failed: {applyException.Message}; rollback start failed: {rollbackException.Message}");
                    }
                }

                throw;
            }
            finally
            {
                DeleteFileIfPresent(stagingPath);
                if (!promoted)
                {
                    DeleteFileIfPresent(backupPath);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_generatedConfig is null || _capabilities is null || _executablePath is null)
            {
                if (_lastSettings is null)
                {
                    throw new InvalidOperationException("There is no saved EasyBalance configuration to restart.");
                }

                await ConfigureAndStartUnderGateAsync(_lastSettings, _lastAdapters, replaceActiveConfig: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            _explicitlyStopped = false;
            await StopProcessUnderGateAsync(markExplicit: false, cancellationToken).ConfigureAwait(false);
            ClearRestartFailures();
            SetFaulted(null);
            await StartProcessUnderGateAsync(_executablePath, GeneratedConfigPath, _generatedConfig, _capabilities, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFaulted(exception.Message);
            AddLog("Error", $"Manual sing-box restart failed: {Sanitize(exception.Message, _generatedConfig?.ApiSecret)}");
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopProcessUnderGateAsync(markExplicit: true, cancellationToken).ConfigureAwait(false);
            SetFaulted(null);
            _controlClient.Clear();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public IReadOnlyList<SingBoxCoreLogEntry> GetRecentLogs(int maximum = 100)
    {
        var count = Math.Clamp(maximum, 0, MaximumRecentLogs);
        lock (_snapshotLock)
        {
            return _recentLogs.Reverse().Take(count).Reverse().ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopProcessUnderGateAsync(markExplicit: true, CancellationToken.None).ConfigureAwait(false);
            _controlClient.Clear();
            if (_controlClient is IDisposable disposableControlClient)
            {
                disposableControlClient.Dispose();
            }

            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ConfigureAndStartUnderGateAsync(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        bool replaceActiveConfig,
        CancellationToken cancellationToken)
    {
        var transaction = await PrepareValidatedConfigurationAsync(settings, adapters, cancellationToken).ConfigureAwait(false);
        try
        {
            if (replaceActiveConfig)
            {
                PromoteConfiguration(transaction.StagingPath, GeneratedConfigPath, backupPath: null);
            }

            _generatedConfig = transaction.Generated;
            _capabilities = transaction.Capabilities;
            _executablePath = transaction.ExecutablePath;
            _lastSettings = settings;
            _lastAdapters = adapters.ToArray();
            await StartProcessUnderGateAsync(
                transaction.ExecutablePath,
                GeneratedConfigPath,
                transaction.Generated,
                transaction.Capabilities,
                cancellationToken).ConfigureAwait(false);
            ClearRestartFailures();
        }
        finally
        {
            DeleteFileIfPresent(transaction.StagingPath);
        }
    }

    private async Task<PreparedConfiguration> PrepareValidatedConfigurationAsync(
        AppSettings settings,
        IReadOnlyCollection<NetworkAdapterInfo> adapters,
        CancellationToken cancellationToken)
    {
        var executablePath = ResolveExecutablePath(settings);
        var capabilities = await _capabilitiesDetector.DetectAsync(executablePath, cancellationToken).ConfigureAwait(false);
        capabilities.EnsureCompatible();
        var generated = _configGenerator.Generate(settings, adapters, capabilities);
        EnsureSecureConfigDirectory();
        var stagingPath = GetStagingPath();
        try
        {
            await WriteSecureConfigAsync(stagingPath, generated.Json, cancellationToken).ConfigureAwait(false);
            var result = await RunCheckAsync(executablePath, stagingPath, generated.ApiSecret, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildCheckError(result));
            }

            return new PreparedConfiguration(executablePath, capabilities, generated, stagingPath);
        }
        catch
        {
            DeleteFileIfPresent(stagingPath);
            throw;
        }
    }

    private async Task StartProcessUnderGateAsync(
        string executablePath,
        string configPath,
        SingBoxGeneratedConfig generated,
        SingBoxCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is { Length: > 0 } line)
            {
                AddLog("Information", Sanitize(line, generated.ApiSecret));
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is { Length: > 0 } line)
            {
                AddLog("Warning", Sanitize(line, generated.ApiSecret));
            }
        };

        var processStarted = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("sing-box process could not be started.");
            }

            processStarted = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _processGeneration++;
            var generation = _processGeneration;
            _controlClient.Configure(new Uri($"http://127.0.0.1:{generated.ApiPort}/"), generated.ApiSecret);
            await WaitForControlApiAsync(process, cancellationToken).ConfigureAwait(false);
            lock (_snapshotLock)
            {
                _coreRunning = true;
                _faulted = false;
                _lastError = null;
                _version = capabilities.VersionText;
                _startedAt = DateTimeOffset.UtcNow;
                _lastStartedAt = _startedAt;
            }

            AddLog("Information", $"sing-box {capabilities.VersionText} started.");
            _ = MonitorProcessAsync(process, generation);
        }
        catch
        {
            _controlClient.Clear();
            if (processStarted && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Keep the original startup exception.
                }
            }

            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }

            if (processStarted)
            {
                lock (_snapshotLock)
                {
                    _lastExitCode = SafeExitCode(process);
                }
            }

            process.Dispose();
            throw;
        }
    }

    private async Task WaitForControlApiAsync(Process process, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"sing-box exited during startup with code {process.ExitCode}.");
            }

            try
            {
                var outbounds = await _controlClient.GetOutboundsAsync(cancellationToken).ConfigureAwait(false);
                if (outbounds.Count > 0)
                {
                    return;
                }

                lastError = new InvalidDataException("The sing-box control API returned no configured outbounds.");
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The sing-box local control API did not become ready within five seconds.", lastError);
    }

    private async Task VerifyStartupSurvivalAsync(TimeSpan stabilityWindow, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("sing-box process disappeared during configuration apply.");
        if (process.HasExited)
        {
            throw new InvalidOperationException($"sing-box exited during configuration apply with code {SafeExitCode(process)}.");
        }

        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var delayTask = Task.Delay(stabilityWindow, cancellationToken);
        var completedTask = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        if (completedTask == exitTask)
        {
            await exitTask.ConfigureAwait(false);
            throw new InvalidOperationException($"sing-box exited during configuration apply with code {SafeExitCode(process)}.");
        }

        await delayTask.ConfigureAwait(false);
        if (process.HasExited)
        {
            throw new InvalidOperationException($"sing-box exited during configuration apply with code {SafeExitCode(process)}.");
        }
    }

    private async Task MonitorProcessAsync(Process process, long generation)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var exitCode = SafeExitCode(process);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        TimeSpan? delay = null;
        try
        {
            if (_disposed || generation != _processGeneration || !ReferenceEquals(_process, process))
            {
                return;
            }

            var uptime = _startedAt is { } startedAt ? DateTimeOffset.UtcNow - startedAt : TimeSpan.Zero;
            process.Dispose();
            _process = null;
            _controlClient.Clear();
            lock (_snapshotLock)
            {
                _lastExitCode = exitCode;
            }
            SetCoreStopped($"sing-box exited unexpectedly with code {exitCode}.");

            AddLog("Error", $"sing-box exited unexpectedly with code {exitCode}.");
            _logger.LogWarning("sing-box exited unexpectedly with code {ExitCode}.", exitCode);

            if (uptime >= StableRunDuration)
            {
                ClearRestartFailures();
            }

            var now = DateTimeOffset.UtcNow;
            while (_recentFailures.TryPeek(out var failure) && now - failure > RestartWindow)
            {
                _recentFailures.Dequeue();
            }

            _recentFailures.Enqueue(now);
            if (_recentFailures.Count >= FaultAfterFailuresInWindow || _explicitlyStopped)
            {
                SetFaulted($"sing-box stopped after repeated unexpected exits (last exit code {exitCode}).");
                return;
            }

            var backoffIndex = Math.Min(_recentFailures.Count - 1, RestartBackoff.Length - 1);
            delay = RestartBackoff[backoffIndex];
            SetCoreStopped($"sing-box exited unexpectedly with code {exitCode}; restarting after {delay.Value.TotalSeconds:0} seconds.");
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (delay is null)
        {
            return;
        }

        AddLog("Warning", $"Restarting sing-box in {delay.Value.TotalSeconds:0} seconds.");
        await Task.Delay(delay.Value).ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _explicitlyStopped || generation != _processGeneration || _process is not null ||
                _generatedConfig is null || _capabilities is null || _executablePath is null)
            {
                return;
            }

            try
            {
                await StartProcessUnderGateAsync(
                    _executablePath,
                    GeneratedConfigPath,
                    _generatedConfig,
                    _capabilities,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Automatic sing-box restart attempt failed.");
                AddLog("Error", $"Automatic sing-box restart failed: {Sanitize(exception.Message, _generatedConfig.ApiSecret)}");
                SetCoreStopped(Sanitize(exception.Message, _generatedConfig.ApiSecret));
                // Treat a failed launch as another crash attempt and follow the same bounded backoff.
                var retryProcess = _process;
                if (retryProcess is null)
                {
                    _processGeneration++;
                }

                _ = ScheduleRestartAfterFailedLaunchAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ScheduleRestartAfterFailedLaunchAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _explicitlyStopped || _generatedConfig is null || _capabilities is null || _executablePath is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            while (_recentFailures.TryPeek(out var failure) && now - failure > RestartWindow)
            {
                _recentFailures.Dequeue();
            }

            _recentFailures.Enqueue(now);
            if (_recentFailures.Count >= FaultAfterFailuresInWindow)
            {
                SetFaulted("sing-box reached the automatic restart limit.");
                return;
            }

            var delay = RestartBackoff[Math.Min(_recentFailures.Count - 1, RestartBackoff.Length - 1)];
            var generation = _processGeneration;
            _ = RestartAfterDelayAsync(delay, generation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RestartAfterDelayAsync(TimeSpan delay, long generation)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _explicitlyStopped || generation != _processGeneration || _process is not null ||
                _generatedConfig is null || _capabilities is null || _executablePath is null)
            {
                return;
            }

            await StartProcessUnderGateAsync(
                _executablePath,
                GeneratedConfigPath,
                _generatedConfig,
                _capabilities,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Automatic sing-box restart attempt failed.");
            AddLog("Error", $"Automatic sing-box restart failed: {Sanitize(exception.Message, _generatedConfig?.ApiSecret)}");
            SetCoreStopped(Sanitize(exception.Message, _generatedConfig?.ApiSecret));
            _ = ScheduleRestartAfterFailedLaunchAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopProcessUnderGateAsync(bool markExplicit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (markExplicit)
        {
            _explicitlyStopped = true;
        }

        _processGeneration++;
        var process = _process;
        _process = null;
        _controlClient.Clear();
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                lock (_snapshotLock)
                {
                    _lastExitCode = SafeExitCode(process);
                }
            }
            catch (InvalidOperationException)
            {
                // The child may have exited between HasExited and Kill.
            }
            finally
            {
                process.Dispose();
            }
        }

        SetCoreStopped(null);
        AddLog("Information", markExplicit ? "sing-box stopped by request." : "sing-box stopped for a configuration change.");
    }

    private async Task<CheckResult> RunCheckAsync(
        string executablePath,
        string configPath,
        string secret,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("check");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("sing-box configuration check could not be started.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Could not start sing-box configuration check.", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new CheckResult(
                process.ExitCode,
                Sanitize(await stdoutTask.ConfigureAwait(false), secret),
                Sanitize(await stderrTask.ConfigureAwait(false), secret));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("sing-box config check did not finish within 30 seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static string BuildCheckError(CheckResult result)
    {
        var detail = string.Join(Environment.NewLine, new[] { result.StandardError, result.StandardOutput }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (detail.Length > 1500)
        {
            detail = detail[..1500];
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"sing-box rejected the generated configuration (exit code {result.ExitCode})."
            : $"sing-box rejected the generated configuration (exit code {result.ExitCode}): {detail}";
    }

    private static void PromoteConfiguration(string stagingPath, string activePath, string? backupPath)
    {
        if (!File.Exists(activePath))
        {
            File.Move(stagingPath, activePath);
            return;
        }

        if (backupPath is null)
        {
            File.Replace(stagingPath, activePath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Replace(stagingPath, activePath, backupPath, ignoreMetadataErrors: true);
        }
    }

    private static void RestoreConfiguration(string activePath, string backupPath)
    {
        if (File.Exists(backupPath))
        {
            if (File.Exists(activePath))
            {
                File.Replace(backupPath, activePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(backupPath, activePath);
            }
        }
        else
        {
            DeleteFileIfPresent(activePath);
        }
    }

    private static async Task WriteSecureConfigAsync(string path, string json, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureSecureConfigDirectory()
    {
        var directoryPath = Path.GetDirectoryName(GetDefaultConfigPath())!;
        Directory.CreateDirectory(directoryPath);
        EnsureNoReparsePoints(directoryPath);

        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } serviceIdentity)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                serviceIdentity,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        new DirectoryInfo(directoryPath).SetAccessControl(security);
    }

    private static string GetDefaultConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EasyBalance",
        "generated",
        "sing-box.json");

    private static string GetStagingPath() => GetDefaultConfigPath() + ".stage-" + Guid.NewGuid().ToString("N");

    private static string ResolveExecutablePath(AppSettings settings)
    {
        var configuredPath = string.IsNullOrWhiteSpace(settings.SingBoxPath)
            ? Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe")
            : settings.SingBoxPath;
        var fullPath = Path.GetFullPath(configuredPath);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new SecurityException("sing-box cannot be launched from a network share.");
        }

        if (!string.Equals(Path.GetFileName(fullPath), "sing-box.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("The configured core executable must be named sing-box.exe.");
        }

        var allowedRoots = new[]
            {
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trustedRoot = allowedRoots.FirstOrDefault(root => IsWithinRoot(fullPath, root));
        if (trustedRoot is null)
        {
            throw new SecurityException("sing-box must be stored under the EasyBalance installation folder or Program Files.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected sing-box executable was not found.", fullPath);
        }

        EnsureNoReparsePoints(fullPath);
        EnsureProtectedPath(fullPath, trustedRoot);
        return fullPath;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var volumeRoot = Path.GetPathRoot(path) ?? throw new SecurityException("sing-box path has no local volume root.");
        var relative = Path.GetRelativePath(volumeRoot, path);
        var current = Path.GetFullPath(volumeRoot);
        CheckNoReparsePoint(current);
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) || Directory.Exists(current))
            {
                CheckNoReparsePoint(current);
            }
        }
    }

    private static void CheckNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException("sing-box executable paths cannot contain symbolic links or reparse points.");
        }
    }

    private static void EnsureProtectedPath(string path, string trustedRoot)
    {
        var trustedOwners = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
        };
        if (WindowsIdentity.GetCurrent().User is { } serviceIdentity)
        {
            trustedOwners.Add(serviceIdentity);
        }

        var current = path;
        var volumeRoot = Path.GetPathRoot(path) ?? throw new SecurityException("sing-box path has no local volume root.");
        while (true)
        {
            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            FileSystemSecurity access = Directory.Exists(current)
                ? new DirectoryInfo(current).GetAccessControl(sections)
                : new FileInfo(current).GetAccessControl(sections);
            if (access.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trustedOwners.Contains(owner))
            {
                throw new SecurityException("sing-box and its parent folders must be owned by the service account, SYSTEM, or Administrators.");
            }

            var isInsideInstallRoot = IsWithinRoot(current, trustedRoot);
            var disallowedRights = isInsideInstallRoot
                ? FileSystemRights.Write | FileSystemRights.Modify | FileSystemRights.FullControl |
                  FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                  FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
                : FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                  FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in access.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference is SecurityIdentifier sid && !trustedOwners.Contains(sid) &&
                    (rule.FileSystemRights & disallowedRights) != 0)
                {
                    throw new SecurityException(isInsideInstallRoot
                        ? "sing-box and its installation folders must not be writable by ordinary users."
                        : "The parent folders of the sing-box installation must not allow ordinary users to replace the installation root.");
                }
            }

            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            current = parent;
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private int? GetCurrentProcessId()
    {
        try
        {
            return _coreRunning && _process is { HasExited: false } process ? process.Id : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }

    private static string Sanitize(string value, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Replace(secret, "[redacted]", StringComparison.Ordinal)
            .Replace($"Bearer {secret}", "Bearer [redacted]", StringComparison.Ordinal);
    }

    private void SetCoreStopped(string? error)
    {
        lock (_snapshotLock)
        {
            _coreRunning = false;
            _startedAt = null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastError = error;
            }
        }
    }

    private void SetFaulted(string? error)
    {
        lock (_snapshotLock)
        {
            _faulted = error is not null;
            _coreRunning = false;
            _startedAt = null;
            _lastError = error;
        }
    }

    private void ClearRestartFailures()
    {
        _recentFailures.Clear();
    }

    private void AddLog(string level, string message)
    {
        var entry = new SingBoxCoreLogEntry(DateTimeOffset.UtcNow, level, message);
        lock (_snapshotLock)
        {
            _recentLogs.Enqueue(entry);
            while (_recentLogs.Count > MaximumRecentLogs)
            {
                _recentLogs.Dequeue();
            }
        }

        if (string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("{CoreLogMessage}", message);
        }
        else if (string.Equals(level, "Warning", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("{CoreLogMessage}", message);
        }
        else
        {
            _logger.LogInformation("{CoreLogMessage}", message);
        }

        try
        {
            LogReceived?.Invoke(this, entry);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A sing-box log event subscriber failed.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record CheckResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record PreparedConfiguration(
        string ExecutablePath,
        SingBoxCapabilities Capabilities,
        SingBoxGeneratedConfig Generated,
        string StagingPath);
}

public sealed record SingBoxCoreSnapshot(
    bool CoreRunning,
    bool Faulted,
    string? Version,
    TimeSpan? Uptime,
    string? LastError,
    int? LastExitCode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastStartedAt,
    int? ProcessId = null)
{
    public bool CoreFaulted => Faulted;
}

public sealed class SingBoxCoreLogEntry : EventArgs
{
    public SingBoxCoreLogEntry(DateTimeOffset timestamp, string level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }
    public string Level { get; }
    public string Message { get; }

    public override string ToString() => $"[{Timestamp:O}] {Level}: {Message}";
}
