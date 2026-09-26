using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using EasyBalance.UI.Core;
using EasyBalance.UI.Models;
using EasyBalance.UI.Services;

namespace EasyBalance.UI.ViewModels;

public sealed class TrafficViewModel : PageViewModel
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _ratioGate = new(1, 1);
    private readonly Dictionary<string, (long upload, long download, DateTimeOffset time)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loopCancellation;
    private bool _policyLoaded;
    private PolicyRow? _defaultPolicy;
    private CancellationTokenSource? _ratioCancellation;

    public TrafficViewModel(PipeClient service) : base(service, "Live traffic", "Watch every WAN exit and the connections using it.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SaveBalanceCommand = new AsyncRelayCommand(_ => SaveBalanceAsync(), _ => !IsSaving && IsBalanceDirty && IsBalanceAvailable);
        ResetBalanceCommand = new RelayCommand(_ => ResetBalance(), _ => IsBalanceDirty && !IsSaving);
        VisibleConnections = CollectionViewSource.GetDefaultView(Connections);
        VisibleConnections.Filter = FilterConnection;
    }

    public ObservableCollection<LiveOutboundRow> Outbounds { get; } = [];
    public ObservableCollection<ActiveConnectionRow> Connections { get; } = [];
    public ICollectionView VisibleConnections { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveBalanceCommand { get; }
    public RelayCommand ResetBalanceCommand { get; }

    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) VisibleConnections.Refresh(); } }
    private string _sampleText = "Waiting for first sample";
    public string SampleText { get => _sampleText; private set => SetProperty(ref _sampleText, value); }
    private string _telemetryMessage = "Connecting to local service…";
    public string TelemetryMessage { get => _telemetryMessage; private set => SetProperty(ref _telemetryMessage, value); }
    private string _uploadedTotal = "—";
    public string UploadedTotal { get => _uploadedTotal; private set => SetProperty(ref _uploadedTotal, value); }
    private string _downloadedTotal = "—";
    public string DownloadedTotal { get => _downloadedTotal; private set => SetProperty(ref _downloadedTotal, value); }
    private string _connectionCount = "—";
    public string ConnectionCount { get => _connectionCount; private set => SetProperty(ref _connectionCount, value); }
    private string _primaryName = "Primary WAN";
    public string PrimaryName { get => _primaryName; private set => SetProperty(ref _primaryName, value); }
    private string _fallbackName = "Fallback WAN";
    public string FallbackName { get => _fallbackName; private set => SetProperty(ref _fallbackName, value); }
    private string _balanceStatus = "Loading default policy…";
    public string BalanceStatus { get => _balanceStatus; private set => SetProperty(ref _balanceStatus, value); }
    private bool _isBalanceAvailable;
    public bool IsBalanceAvailable { get => _isBalanceAvailable; private set => SetProperty(ref _isBalanceAvailable, value); }
    private bool _isSaving;
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) UpdateCommands(); } }
    private bool _isBalanceDirty;
    public bool IsBalanceDirty { get => _isBalanceDirty; private set { if (SetProperty(ref _isBalanceDirty, value)) UpdateCommands(); } }
    private int _primaryPercent = 50;
    public int PrimaryPercent
    {
        get => _primaryPercent;
        set
        {
            if (SetProperty(ref _primaryPercent, Math.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(FallbackPercent));
                OnPropertyChanged(nameof(PrimaryPreview));
                OnPropertyChanged(nameof(FallbackPreview));
                IsBalanceDirty = _defaultPolicy is not null && _primaryPercent != _defaultPolicy.PrimaryTrafficPercent;
                if (_policyLoaded && IsBalanceAvailable && IsBalanceDirty) QueueRatioApply();
            }
        }
    }
    public int FallbackPercent => 100 - PrimaryPercent;
    public string PrimaryPreview => $"{PrimaryPercent}%";
    public string FallbackPreview => $"{FallbackPercent}%";

    public override async Task RefreshAsync()
    {
        if (!_policyLoaded)
        {
            await LoadPolicyAsync();
        }
        await RefreshTelemetryAsync();
    }

    public void SetTelemetryActive(bool active)
    {
        if (active)
        {
            if (_loopCancellation is { IsCancellationRequested: false }) return;
            if (!IsBalanceDirty) _policyLoaded = false;
            var cancellation = new CancellationTokenSource();
            _loopCancellation = cancellation;
            _ = RunLoopAsync(cancellation);
            return;
        }

        var current = _loopCancellation;
        _loopCancellation = null;
        current?.Cancel();
        _previous.Clear();
    }

    private async Task RunLoopAsync(CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            await RefreshAsync();
            while (await timer.WaitForNextTickAsync(cancellation.Token)) await RefreshTelemetryAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private async Task LoadPolicyAsync()
    {
        try
        {
            var settings = await Service.CallAsync("GetSettings");
            var policyJson = JsonValue.Property(settings, "DefaultPolicy");
            if (policyJson.ValueKind != JsonValueKind.Object)
            {
                BalanceStatus = "Default policy is unavailable.";
                return;
            }
            var policy = PolicyRow.FromJson(policyJson);
            _defaultPolicy = policy;
            PrimaryPercent = policy.PrimaryTrafficPercent;
            IsBalanceDirty = false;
            var adapters = await Service.CallAsync("GetAdapters");
            var names = JsonValue.Items(adapters).Select(AdapterRow.FromJson).GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First().Name);
            PrimaryName = names.GetValueOrDefault(policy.PrimaryInterfaceId, "Primary WAN");
            FallbackName = policy.FallbackInterfaceId is { } fallback ? names.GetValueOrDefault(fallback, "Fallback WAN") : "No fallback selected";
            IsBalanceAvailable = policy.FallbackInterfaceId is not null && policy.LoadBalanceEnabled;
            BalanceStatus = policy.FallbackInterfaceId is null
                ? "Choose a fallback interface in Policies to balance traffic."
                : policy.LoadBalanceEnabled ? "New connections use the saved target. Existing flows stay on their current exit." : "Enable load balancing for the default policy in Policies.";
            _policyLoaded = true;
        }
        catch (Exception ex)
        {
            BalanceStatus = $"Policy unavailable: {ex.Message}";
        }
    }

    private async Task RefreshTelemetryAsync()
    {
        if (!await _requestGate.WaitAsync(0)) return;
        try
        {
            var telemetry = await Service.CallAsync("GetConnectionTelemetry");
            var now = DateTimeOffset.UtcNow;
            UploadedTotal = TelemetryFormat.Bytes(telemetry, "UploadTotal");
            DownloadedTotal = TelemetryFormat.Bytes(telemetry, "DownloadTotal");
            SampleText = $"Updated {TelemetryFormat.DateTime(telemetry, "SampledAt")} · every 2 s";
            var outboundJson = JsonValue.Property(telemetry, "Outbounds");
            if (outboundJson.ValueKind != JsonValueKind.Array)
            {
                Outbounds.Clear();
                TelemetryMessage = "Outbound counters are unavailable.";
            }
            else
            {
                var samples = new List<(JsonElement item, double? uploadRate, double? downloadRate)>();
                foreach (var item in JsonValue.Items(outboundJson))
                {
                    var id = TelemetryFormat.Text(item, "InterfaceId");
                    var upload = JsonValue.Int64(item, -1, "UploadBytes");
                    var download = JsonValue.Int64(item, -1, "DownloadBytes");
                    double? uploadRate = null, downloadRate = null;
                    if (upload >= 0 && download >= 0 && _previous.TryGetValue(id, out var previous))
                    {
                        var seconds = (now - previous.time).TotalSeconds;
                        if (seconds > 0.2 && upload >= previous.upload && download >= previous.download)
                        {
                            uploadRate = (upload - previous.upload) / seconds;
                            downloadRate = (download - previous.download) / seconds;
                        }
                    }
                    if (upload >= 0 && download >= 0) _previous[id] = (upload, download, now);
                    samples.Add((item, uploadRate, downloadRate));
                }
                var totalRate = samples.Sum(sample => (sample.uploadRate ?? 0) + (sample.downloadRate ?? 0));
                var rows = samples.Select(sample => LiveOutboundRow.FromJson(sample.item, sample.uploadRate, sample.downloadRate,
                    totalRate > 0 && sample.uploadRate is not null && sample.downloadRate is not null
                        ? (sample.uploadRate.Value + sample.downloadRate.Value) / totalRate * 100
                        : null)).ToList();
                Replace(Outbounds, rows);
                TelemetryMessage = rows.Count == 0 ? "No WAN exits were reported by the service." : string.Empty;
            }

            var connectionJson = JsonValue.Property(telemetry, "Connections");
            if (connectionJson.ValueKind == JsonValueKind.Array)
            {
                Replace(Connections, JsonValue.Items(connectionJson).Select(ActiveConnectionRow.FromJson));
                ConnectionCount = Connections.Count.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Connections.Clear();
                ConnectionCount = "—";
                TelemetryMessage = "Connection details are unavailable.";
            }
            var lastError = TelemetryFormat.Text(telemetry, "LastError");
            if (!string.Equals(lastError, "Unknown", StringComparison.OrdinalIgnoreCase)) TelemetryMessage = lastError;
        }
        catch (Exception ex)
        {
            Outbounds.Clear();
            Connections.Clear();
            _previous.Clear();
            UploadedTotal = DownloadedTotal = ConnectionCount = "—";
            SampleText = "No current sample";
            TelemetryMessage = $"Live telemetry unavailable: {ex.Message}";
        }
        finally { _requestGate.Release(); }
    }

    private void QueueRatioApply()
    {
        _ratioCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _ratioCancellation = cancellation;
        BalanceStatus = "Applying target after you finish dragging…";
        _ = ApplyAfterDelayAsync(cancellation);
    }

    private async Task ApplyAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(450, cancellation.Token);
            await ApplyRatioAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_ratioCancellation, cancellation)) _ratioCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task SaveBalanceAsync()
    {
        _ratioCancellation?.Cancel();
        await ApplyRatioAsync(CancellationToken.None);
    }

    private async Task ApplyRatioAsync(CancellationToken cancellationToken)
    {
        if (_defaultPolicy is null || !IsBalanceAvailable || !IsBalanceDirty) return;
        await _ratioGate.WaitAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested) { _ratioGate.Release(); return; }
        var selectedPercent = PrimaryPercent;
        IsSaving = true;
        ErrorMessage = string.Empty;
        Message = string.Empty;
        BalanceStatus = "Applying traffic split…";
        try
        {
            // Once sent, let the request finish so the UI can report a definite saved target.
            await Service.CallAsync("SetTrafficRatio", new { primaryTrafficPercent = selectedPercent });
            _defaultPolicy.PrimaryTrafficPercent = selectedPercent;
            IsBalanceDirty = PrimaryPercent != selectedPercent;
            if (!IsBalanceDirty)
            {
                BalanceStatus = "Applied and saved. New connections follow this target; existing flows stay on their exit.";
                Message = $"Applied: {PrimaryName} {selectedPercent}% · {FallbackName} {100 - selectedPercent}%.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            BalanceStatus = "Could not apply this target. The preview remains unsaved; retry or reset.";
        }
        finally { IsSaving = false; _ratioGate.Release(); }
    }

    private void ResetBalance()
    {
        if (_defaultPolicy is null) return;
        _ratioCancellation?.Cancel();
        PrimaryPercent = _defaultPolicy.PrimaryTrafficPercent;
        IsBalanceDirty = false;
        BalanceStatus = "Preview reset to the last applied target.";
        ErrorMessage = string.Empty;
    }

    private bool FilterConnection(object item)
    {
        if (item is not ActiveConnectionRow row || string.IsNullOrWhiteSpace(SearchText)) return true;
        return new[] { row.Process, row.ProcessPath, row.DestinationIp, row.Host, row.ActualOutbound, row.PredictedOutbound }
            .Any(value => value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateCommands()
    {
        SaveBalanceCommand.RaiseCanExecuteChanged();
        ResetBalanceCommand.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}

public sealed class LiveOutboundRow
{
    public string Name { get; init; } = "Unknown WAN";
    public string InterfaceId { get; init; } = "—";
    public string Availability { get; init; } = "Unknown";
    public bool? IsAvailable { get; init; }
    public string UploadRate { get; init; } = "Sampling…";
    public string DownloadRate { get; init; } = "Sampling…";
    public string Uploaded { get; init; } = "—";
    public string Downloaded { get; init; } = "—";
    public string Connections { get; init; } = "—";
    public string Target { get; init; } = "—";
    public string ObservedShare { get; init; } = "Sampling…";
    public string Error { get; init; } = string.Empty;

    public static LiveOutboundRow FromJson(JsonElement item, double? uploadRate, double? downloadRate, double? observedShare)
    {
        var available = TelemetryFormat.Boolean(item, "Available");
        var error = TelemetryFormat.Text(item, "Error");
        return new LiveOutboundRow
        {
            Name = TelemetryFormat.Text(item, "Name"),
            InterfaceId = TelemetryFormat.Text(item, "InterfaceId"),
            IsAvailable = available,
            Availability = available switch { true => "Available", false => "Unavailable", _ => "Unknown" },
            UploadRate = FormatRate(uploadRate),
            DownloadRate = FormatRate(downloadRate),
            Uploaded = TelemetryFormat.Bytes(item, "UploadBytes"),
            Downloaded = TelemetryFormat.Bytes(item, "DownloadBytes"),
            Connections = TelemetryFormat.Count(item, "ActiveConnections"),
            Target = TelemetryFormat.Percent(item, "TargetPercent"),
            ObservedShare = observedShare is { } share ? $"{share.ToString("0.#", CultureInfo.InvariantCulture)}%" : "—",
            Error = error == "Unknown" ? string.Empty : error
        };
    }

    private static string FormatRate(double? value)
    {
        if (value is null) return "Sampling…";
        var amount = value.Value;
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var index = 0;
        while (amount >= 1000 && index < units.Length - 1) { amount /= 1000; index++; }
        return $"{amount.ToString(amount < 10 && index > 0 ? "0.0" : "0", CultureInfo.InvariantCulture)} {units[index]}";
    }
}
