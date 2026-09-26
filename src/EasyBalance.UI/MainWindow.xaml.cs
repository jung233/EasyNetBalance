using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyBalance.Shared;
using EasyBalance.UI.Models;
using EasyBalance.UI.Services;
using Microsoft.Win32;

namespace EasyBalance.UI;

public partial class MainWindow : Window
{
    private readonly PipeClient _pipe = new();
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _ratioTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly SemaphoreSlim _ratioGate = new(1, 1);
    private readonly ObservableCollection<OutboundRow> _outbounds = [];
    private readonly ObservableCollection<LiveConnection> _connections = [];
    private readonly ObservableCollection<AdapterRow> _adapters = [];
    private readonly ObservableCollection<RoutingPolicy> _policies = [];
    private readonly ObservableCollection<ApplicationRule> _rules = [];
    private readonly ObservableCollection<RuntimeLogEntry> _logs = [];
    private readonly List<InterfaceChoice> _interfaceChoices = [];
    private AppSettings? _settings;
    private ConnectionTelemetry? _previousSample;
    private RoutingPolicy? _editingPolicy;
    private ApplicationRule? _editingRule;
    private bool _isDark;
    private bool _loadingRatio;
    private bool _monitorBusy;

    public MainWindow()
    {
        InitializeComponent();
        OutboundGrid.ItemsSource = _outbounds;
        ConnectionGrid.ItemsSource = _connections;
        AdapterGrid.ItemsSource = _adapters;
        PolicyList.ItemsSource = _policies;
        RuleGrid.ItemsSource = _rules;
        LogGrid.ItemsSource = _logs;
        _monitorTimer.Tick += async (_, _) => await PollMonitorAsync();
        _ratioTimer.Tick += async (_, _) => await SendRatioAsync();
        Loaded += async (_, _) => await ExecuteAsync(RefreshAllAsync, "Ready");
        Closed += (_, _) => { _monitorTimer.Stop(); _ratioTimer.Stop(); };
    }

    private async Task ExecuteAsync(Func<Task> operation, string success)
    {
        try
        {
            FeedbackText.Text = "Working…";
            await operation();
            if (FeedbackText.Text == "Working…") FeedbackText.Text = success;
        }
        catch (Exception exception)
        {
            FeedbackText.Text = "Error: " + exception.Message;
            try { await RefreshStatusAndLogsAsync(); } catch { }
            LastErrorText.Text = exception.Message;
        }
    }

    private async Task RefreshAllAsync()
    {
        await RefreshStatusAsync();
        await RefreshAdaptersAsync();
        await RefreshSettingsAsync();
        await RefreshRulesAsync();
        await RefreshLogsAsync();
        await RefreshDiagnosticsAsync();
        if (Pages.SelectedIndex == 1) await PollMonitorAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _pipe.GetAsync<RuntimeStatus>("GetStatus");
        RoutingState.Text = status.RoutingEnabled ? status.CoreRunning ? "Active" : "Starting / faulted" : "Off";
        CoreState.Text = status.CoreRunning ? "Running" : status.CoreFaulted ? "Faulted" : "Stopped";
        CoreDetail.Text = $"{status.Version ?? "Version unknown"}  ·  Uptime {status.Uptime?.ToString(@"dd\.hh\:mm\:ss") ?? "—"}";
        LastErrorText.Text = string.IsNullOrWhiteSpace(status.LastError) ? "No active error" : status.LastError;
        RouteGrid.ItemsSource = status.Policies;
    }

    private async Task RefreshAdaptersAsync()
    {
        var selectedId = (AdapterGrid.SelectedItem as AdapterRow)?.Id;
        var items = await _pipe.GetAsync<List<NetworkAdapterInfo>>("GetAdapters");
        _adapters.Clear();
        _interfaceChoices.Clear();
        _interfaceChoices.Add(new InterfaceChoice(null, "No fallback"));
        foreach (var adapter in items.OrderByDescending(item => item.IsUserAllowed).ThenBy(item => item.Name))
        {
            _adapters.Add(new AdapterRow(adapter));
            if (adapter.IsUserAllowed) _interfaceChoices.Add(new InterfaceChoice(adapter.Id, adapter.Name));
        }
        AdapterGrid.SelectedItem = _adapters.FirstOrDefault(item => item.Id == selectedId);
        PrimaryInterface.ItemsSource = _interfaceChoices.Where(item => item.Id is not null).ToList();
        FallbackInterface.ItemsSource = _interfaceChoices.ToList();
        AdapterSummary.Text = $"{items.Count(item => item.IsUserAllowed && item.IsPhysical)} / {items.Count}";
    }

    private async Task RefreshSettingsAsync()
    {
        var selectedId = _editingPolicy?.Id;
        _settings = await _pipe.GetAsync<AppSettings>("GetSettings");
        var items = await _pipe.GetAsync<List<RoutingPolicy>>("GetPolicies");
        _policies.Clear();
        foreach (var item in items) _policies.Add(item);
        PolicyList.SelectedItem = _policies.FirstOrDefault(item => item.Id == selectedId) ?? _policies.FirstOrDefault();
        RulePolicy.ItemsSource = _policies.ToList();
        ConfigureRatio();
    }

    private async Task RefreshRulesAsync()
    {
        var selectedId = _editingRule?.Id;
        var items = await _pipe.GetAsync<List<ApplicationRule>>("GetRules");
        _rules.Clear();
        foreach (var item in items.OrderByDescending(item => item.Priority).ThenBy(item => item.DisplayName)) _rules.Add(item);
        RuleGrid.SelectedItem = _rules.FirstOrDefault(item => item.Id == selectedId);
    }

    private async Task RefreshLogsAsync()
    {
        var items = await _pipe.GetAsync<List<RuntimeLogEntry>>("GetLogs");
        _logs.Clear();
        foreach (var item in items.OrderByDescending(item => item.Timestamp)) _logs.Add(item);
    }

    private async Task RefreshDiagnosticsAsync()
    {
        var data = await _pipe.GetAsync<RuntimeDiagnostics>("GetDiagnostics");
        DiagnosticsText.Text = $"Health probes       {data.HealthProbes} total  ·  {data.SuccessfulProbes} passed  ·  {data.FailedProbes} failed\n" +
                               $"Route changes       {data.FailoverCount} failovers  ·  {data.FailbackCount} failbacks\n" +
                               $"Configuration       {data.PoliciesCount} policies  ·  {data.RulesCount} application rules  ·  {data.GeneratedRouteRules} generated rules\n" +
                               $"Memory              Service {Display.Bytes(data.ServiceWorkingSetBytes)}  ·  Core {(data.SingBoxWorkingSetBytes is long memory ? Display.Bytes(memory) : "—")}";
    }

    private async Task RefreshStatusAndLogsAsync()
    {
        try { await RefreshStatusAsync(); } catch { }
        try { await RefreshLogsAsync(); } catch { }
    }

    private void ConfigureRatio()
    {
        var policy = _settings?.DefaultPolicy;
        var enabled = policy?.LoadBalanceEnabled == true && !string.IsNullOrWhiteSpace(policy.PrimaryInterfaceId)
            && !string.IsNullOrWhiteSpace(policy.FallbackInterfaceId) && policy.PrimaryInterfaceId != policy.FallbackInterfaceId;
        RatioSlider.IsEnabled = enabled;
        RatioHelp.Text = enabled
            ? $"{NameFor(policy!.PrimaryInterfaceId)} receives the left share; {NameFor(policy.FallbackInterfaceId)} receives the right share."
            : "Enable dual WAN load balance on the default policy and choose two different allowed interfaces first.";
        _loadingRatio = true;
        RatioSlider.Value = policy?.PrimaryTrafficPercent ?? 50;
        RatioText.Text = $"{(int)RatioSlider.Value} / {100 - (int)RatioSlider.Value}";
        _loadingRatio = false;
    }

    private string NameFor(string? id) => _interfaceChoices.FirstOrDefault(choice => choice.Id == id)?.Name ?? "Unknown interface";

    private async Task PollMonitorAsync()
    {
        if (_monitorBusy || Pages.SelectedIndex != 1) return;
        _monitorBusy = true;
        try
        {
            var sample = await _pipe.GetAsync<ConnectionTelemetry>("GetConnectionTelemetry");
            var elapsed = _previousSample is null ? 0 : (sample.SampledAt - _previousSample.SampledAt).TotalSeconds;
            var old = _previousSample?.Outbounds.ToDictionary(item => item.InterfaceId, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, OutboundTraffic>(StringComparer.OrdinalIgnoreCase);
            _outbounds.Clear();
            foreach (var item in sample.Outbounds.OrderBy(item => item.Name))
            {
                var previous = old.GetValueOrDefault(item.InterfaceId);
                var valid = elapsed > 0 && elapsed < 30 && previous is not null
                    && item.UploadBytes >= previous.UploadBytes && item.DownloadBytes >= previous.DownloadBytes;
                _outbounds.Add(new OutboundRow
                {
                    Name = item.Name,
                    UploadRate = valid ? Display.Bytes((long)((item.UploadBytes - previous!.UploadBytes) / elapsed)) + "/s" : "Sampling…",
                    DownloadRate = valid ? Display.Bytes((long)((item.DownloadBytes - previous!.DownloadBytes) / elapsed)) + "/s" : "Sampling…",
                    UploadTotal = Display.Bytes(item.UploadBytes), DownloadTotal = Display.Bytes(item.DownloadBytes),
                    Connections = item.ActiveConnections.ToString(), Target = item.TargetPercent is int target ? target + "%" : "—",
                    State = item.Available ? "Available" : "Unavailable"
                });
            }
            _connections.Clear();
            foreach (var item in sample.Connections) _connections.Add(item);
            _previousSample = sample;
            TelemetryMessage.Text = string.IsNullOrWhiteSpace(sample.LastError)
                ? $"Sampled {sample.SampledAt.ToLocalTime():T}  ·  {sample.Connections.Count} active connections"
                : "Telemetry warning: " + sample.LastError;
        }
        catch (Exception exception)
        {
            _previousSample = null;
            TelemetryMessage.Text = "Monitor unavailable: " + exception.Message;
        }
        finally { _monitorBusy = false; }
    }

    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Pages)) return;
        if (Pages.SelectedIndex == 1)
        {
            _previousSample = null;
            _monitorTimer.Start();
            _ = PollMonitorAsync();
        }
        else { _monitorTimer.Stop(); _previousSample = null; }
    }

    private void RatioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RatioText is null) return;
        var value = (int)Math.Round(RatioSlider.Value);
        RatioText.Text = $"{value} / {100 - value}";
        if (_loadingRatio || !RatioSlider.IsEnabled || _settings is null) return;
        _ratioTimer.Stop();
        _ratioTimer.Start();
    }

    private async Task SendRatioAsync()
    {
        _ratioTimer.Stop();
        if (_settings is null || !RatioSlider.IsEnabled) return;
        var value = (int)Math.Round(RatioSlider.Value);
        if (!await _ratioGate.WaitAsync(0)) { _ratioTimer.Start(); return; }
        try
        {
            await _pipe.CallAsync("SetTrafficRatio", new { primaryTrafficPercent = value });
            _settings.DefaultPolicy.PrimaryTrafficPercent = value;
            FeedbackText.Text = $"Traffic target saved: {value}% / {100 - value}%.";
        }
        catch (Exception exception)
        {
            FeedbackText.Text = "Ratio update failed: " + exception.Message;
            await RefreshStatusAndLogsAsync();
            try { await RefreshSettingsAsync(); } catch { }
        }
        finally { _ratioGate.Release(); }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        ThemeManager.Apply(_isDark);
        ThemeButton.Content = _isDark ? "Light mode" : "Dark mode";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RefreshAllAsync, "Updated just now.");
    private async void Enable_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => { await _pipe.CallAsync("EnableRouting"); await RefreshStatusAsync(); }, "Routing enabled.");
    private async void Disable_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => { await _pipe.CallAsync("DisableRouting"); await RefreshStatusAsync(); }, "Routing disabled.");
    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(RefreshLogsAsync, "Logs refreshed.");

    private void AdapterGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterDetail is null) return;
        AdapterDetail.Text = AdapterGrid.SelectedItem is AdapterRow row
            ? $"{row.Source.Description}\nIPv4: {string.Join(", ", row.Source.IPv4Addresses)}  ·  Gateway: {string.Join(", ", row.Source.IPv4Gateways)}\nIPv6: {string.Join(", ", row.Source.IPv6Addresses)}"
            : "Select an interface.";
    }

    private async Task SetAllowedAsync(bool allowed)
    {
        if (AdapterGrid.SelectedItem is not AdapterRow row) throw new InvalidOperationException("Select an interface first.");
        await _pipe.CallAsync("SetInterfaceUsability", new { interfaceId = row.Id, allowed });
        await RefreshAdaptersAsync();
        await RefreshSettingsAsync();
    }
    private async void Allow_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => SetAllowedAsync(true), "Interface allowed.");
    private async void Exclude_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => SetAllowedAsync(false), "Interface excluded.");
    private async Task ProbeAsync(string family)
    {
        if (AdapterGrid.SelectedItem is not AdapterRow row) throw new InvalidOperationException("Select an interface first.");
        var result = await _pipe.GetWithPayloadAsync<InterfaceTestResult>("TestInterface", new { interfaceId = row.Id, family });
        await RefreshAdaptersAsync();
        FeedbackText.Text = $"{row.Name} {family}: {result.Health}, {(result.LatencyMs is double latency ? $"{latency:0} ms" : "latency unavailable")}.";
    }
    private async void Probe4_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => ProbeAsync("IPv4"), "Probe complete.");
    private async void Probe6_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => ProbeAsync("IPv6"), "Probe complete.");

    private void PolicyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PolicyName is null) return;
        _editingPolicy = PolicyList.SelectedItem as RoutingPolicy;
        if (_editingPolicy is null) return;
        PolicyName.Text = _editingPolicy.Name;
        PrimaryInterface.SelectedValue = _editingPolicy.PrimaryInterfaceId;
        FallbackInterface.SelectedValue = _editingPolicy.FallbackInterfaceId;
        PolicyEnabled.IsChecked = _editingPolicy.Enabled;
        FailoverEnabled.IsChecked = _editingPolicy.FailoverEnabled;
        AutoFailback.IsChecked = _editingPolicy.AutoFailback;
        LoadBalanceEnabled.IsChecked = _editingPolicy.LoadBalanceEnabled;
        LoadBalanceEnabled.IsEnabled = _settings?.DefaultPolicy.Id == _editingPolicy.Id;
    }
    private void NewPolicy_Click(object sender, RoutedEventArgs e)
    {
        _editingPolicy = new RoutingPolicy { Name = "New policy", PrimaryInterfaceId = _interfaceChoices.FirstOrDefault(item => item.Id is not null)?.Id ?? "" };
        PolicyList.SelectedItem = null;
        PolicyName.Text = _editingPolicy.Name;
        PrimaryInterface.SelectedValue = _editingPolicy.PrimaryInterfaceId;
        FallbackInterface.SelectedValue = null;
        PolicyEnabled.IsChecked = true;
        FailoverEnabled.IsChecked = true;
        AutoFailback.IsChecked = true;
        LoadBalanceEnabled.IsChecked = false;
        LoadBalanceEnabled.IsEnabled = false;
    }
    private async void SavePolicy_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var policy = _editingPolicy ?? throw new InvalidOperationException("Choose or create a policy first.");
        if (string.IsNullOrWhiteSpace(PolicyName.Text)) throw new InvalidOperationException("Enter a policy name.");
        if (PrimaryInterface.SelectedValue is not string primary) throw new InvalidOperationException("Choose an allowed primary interface.");
        policy.Name = PolicyName.Text.Trim();
        policy.PrimaryInterfaceId = primary;
        policy.FallbackInterfaceId = FallbackInterface.SelectedValue as string;
        policy.Enabled = PolicyEnabled.IsChecked == true;
        policy.FailoverEnabled = FailoverEnabled.IsChecked == true;
        policy.AutoFailback = AutoFailback.IsChecked == true;
        policy.LoadBalanceEnabled = _settings?.DefaultPolicy.Id == policy.Id && LoadBalanceEnabled.IsChecked == true;
        if (policy.LoadBalanceEnabled && (string.IsNullOrWhiteSpace(policy.FallbackInterfaceId) || policy.FallbackInterfaceId == policy.PrimaryInterfaceId))
            throw new InvalidOperationException("Dual WAN needs two different allowed interfaces.");
        await _pipe.CallAsync("SavePolicy", policy);
        await RefreshSettingsAsync();
        await RefreshStatusAsync();
    }, "Policy saved.");
    private async void DefaultPolicy_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (PolicyList.SelectedItem is not RoutingPolicy policy) throw new InvalidOperationException("Select a policy first.");
        await _pipe.CallAsync("SetDefaultPolicy", new { id = policy.Id });
        await RefreshSettingsAsync();
        await RefreshStatusAsync();
    }, "Default policy changed.");
    private async void DeletePolicy_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (PolicyList.SelectedItem is not RoutingPolicy policy) throw new InvalidOperationException("Select a policy first.");
        if (_settings?.DefaultPolicy.Id == policy.Id) throw new InvalidOperationException("The default policy cannot be deleted.");
        await _pipe.CallAsync("DeletePolicy", new { id = policy.Id });
        _editingPolicy = null;
        await RefreshSettingsAsync();
    }, "Policy deleted.");

    private void RuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleName is null) return;
        _editingRule = RuleGrid.SelectedItem as ApplicationRule;
        if (_editingRule is null) return;
        RuleName.Text = _editingRule.DisplayName;
        RulePath.Text = _editingRule.ExecutablePath ?? "";
        RuleProcess.Text = _editingRule.ProcessName ?? "";
        RulePolicy.SelectedValue = _editingRule.PolicyId;
        RuleEnabled.IsChecked = _editingRule.Enabled;
    }
    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        _editingRule = new ApplicationRule { PolicyId = _settings?.DefaultPolicy.Id ?? "" };
        RuleGrid.SelectedItem = null;
        RuleName.Text = ""; RulePath.Text = ""; RuleProcess.Text = "";
        RulePolicy.SelectedValue = _editingRule.PolicyId; RuleEnabled.IsChecked = true;
    }
    private void BrowseRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            RulePath.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(RuleName.Text)) RuleName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }
    private async void SaveRule_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var rule = _editingRule ?? new ApplicationRule();
        if (string.IsNullOrWhiteSpace(RulePath.Text) && string.IsNullOrWhiteSpace(RuleProcess.Text))
            throw new InvalidOperationException("Enter an executable path or process name.");
        if (RulePolicy.SelectedValue is not string policyId) throw new InvalidOperationException("Choose a policy.");
        rule.DisplayName = string.IsNullOrWhiteSpace(RuleName.Text) ? Path.GetFileNameWithoutExtension(RulePath.Text) : RuleName.Text.Trim();
        rule.ExecutablePath = string.IsNullOrWhiteSpace(RulePath.Text) ? null : RulePath.Text.Trim();
        rule.ProcessName = string.IsNullOrWhiteSpace(RuleProcess.Text) ? null : RuleProcess.Text.Trim();
        rule.PolicyId = policyId;
        rule.Enabled = RuleEnabled.IsChecked == true;
        await _pipe.CallAsync("SaveRule", rule);
        _editingRule = rule;
        await RefreshRulesAsync();
    }, "Rule saved.");
    private async void DeleteRule_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        if (RuleGrid.SelectedItem is not ApplicationRule rule) throw new InvalidOperationException("Select a rule first.");
        await _pipe.CallAsync("DeleteRule", new { id = rule.Id });
        _editingRule = null;
        await RefreshRulesAsync();
    }, "Rule deleted.");

    private async void Validate_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => await _pipe.CallAsync("ValidateConfig"), "Configuration is valid.");
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () => { await _pipe.CallAsync("RestartCore"); await RefreshStatusAsync(); }, "Core restarted.");
    private async void Export_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        var data = await _pipe.GetWithPayloadAsync<DiagnosticsBlob>("ExportDiagnostics", new { redactIpAddresses = RedactIps.IsChecked == true });
        var dialog = new SaveFileDialog { FileName = data.FileName, Filter = "ZIP archive (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) == true) await File.WriteAllBytesAsync(dialog.FileName, Convert.FromBase64String(data.DataBase64));
    }, "Diagnostics exported.");

    public sealed record InterfaceChoice(string? Id, string Name);
    public sealed class AdapterRow(NetworkAdapterInfo source)
    {
        public NetworkAdapterInfo Source { get; } = source;
        public string Id => Source.Id;
        public string Name => Source.Name;
        public string OperationalStatus => Source.OperationalStatus.ToString();
        public string IPv4Health => Source.IPv4Health.ToString();
        public string IPv6Health => Source.IPv6Health.ToString();
        public string IPv4Address => string.Join(", ", Source.IPv4Addresses);
        public string IPv4Gateway => string.Join(", ", Source.IPv4Gateways);
        public string AllowedText => Source.IsUserAllowed ? "Yes" : "No";
    }
    public sealed class InterfaceTestResult
    {
        public string Health { get; set; } = "Unknown";
        public double? LatencyMs { get; set; }
    }
    public sealed class DiagnosticsBlob
    {
        public string FileName { get; set; } = "EasyBalance-diagnostics.zip";
        public string DataBase64 { get; set; } = "";
    }
}
