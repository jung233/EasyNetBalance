using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using EasyBalance.UI.Core;
using EasyBalance.UI.Models;
using EasyBalance.UI.Services;
using Microsoft.Win32;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonNodeValue = System.Text.Json.Nodes.JsonValue;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace EasyBalance.UI.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(PipeClient service, string title, string description)
    {
        Service = service;
        Title = title;
        Description = description;
    }

    protected PipeClient Service { get; }
    public string Title { get; }
    public string Description { get; }
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; protected set => SetProperty(ref _isBusy, value); }
    private string _errorMessage = string.Empty;
    public string ErrorMessage { get => _errorMessage; protected set => SetProperty(ref _errorMessage, value); }
    private string _message = string.Empty;
    public string Message { get => _message; protected set => SetProperty(ref _message, value); }
    public virtual Task RefreshAsync() => Task.CompletedTask;

    protected async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        Message = string.Empty;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ThemeManager _themeManager = new();
    public MainWindowViewModel()
    {
        Service = new PipeClient();
        Dashboard = new DashboardViewModel(Service);
        Rules = new RulesViewModel(Service);
        Interfaces = new InterfacesViewModel(Service);
        Failover = new FailoverViewModel(Service);
        Logs = new LogsViewModel(Service);
        Advanced = new AdvancedViewModel(Service);
        Diagnostics = new DiagnosticsViewModel(Service);
        CurrentPage = Dashboard;
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString()));
        RefreshCommand = new AsyncRelayCommand(_ => RefreshCurrentAsync());
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
    }

    private PipeClient Service { get; }
    public DashboardViewModel Dashboard { get; }
    public RulesViewModel Rules { get; }
    public InterfacesViewModel Interfaces { get; }
    public FailoverViewModel Failover { get; }
    public LogsViewModel Logs { get; }
    public AdvancedViewModel Advanced { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    private PageViewModel _currentPage = null!;
    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsDashboardActive));
                OnPropertyChanged(nameof(IsRulesActive));
                OnPropertyChanged(nameof(IsInterfacesActive));
                OnPropertyChanged(nameof(IsFailoverActive));
                OnPropertyChanged(nameof(IsLogsActive));
                OnPropertyChanged(nameof(IsAdvancedActive));
                OnPropertyChanged(nameof(IsDiagnosticsActive));
            }
        }
    }

    public bool IsDashboardActive => ReferenceEquals(CurrentPage, Dashboard);
    public bool IsRulesActive => ReferenceEquals(CurrentPage, Rules);
    public bool IsInterfacesActive => ReferenceEquals(CurrentPage, Interfaces);
    public bool IsFailoverActive => ReferenceEquals(CurrentPage, Failover);
    public bool IsLogsActive => ReferenceEquals(CurrentPage, Logs);
    public bool IsAdvancedActive => ReferenceEquals(CurrentPage, Advanced);
    public bool IsDiagnosticsActive => ReferenceEquals(CurrentPage, Diagnostics);
    public string ThemeButtonText => _themeManager.IsDark ? "☀  Light" : "☾  Dark";

    private string _connectionLabel = "Service not connected";
    public string ConnectionLabel { get => _connectionLabel; private set => SetProperty(ref _connectionLabel, value); }

    public async Task InitializeAsync() => await RefreshCurrentAsync();

    public async Task RefreshCurrentAsync()
    {
        await CurrentPage.RefreshAsync();
        var page = CurrentPage;
        ConnectionLabel = string.IsNullOrEmpty(page.ErrorMessage) ? "Service connected" : "Service unavailable";
    }

    public async Task RefreshDashboardAsync()
    {
        await Dashboard.RefreshAsync();
        if (ReferenceEquals(CurrentPage, Dashboard))
        {
            ConnectionLabel = string.IsNullOrEmpty(Dashboard.ErrorMessage) ? "Service connected" : "Service unavailable";
        }
    }

    private void Navigate(string? name)
    {
        CurrentPage = name switch
        {
            "Rules" => Rules,
            "Interfaces" => Interfaces,
            "Failover" => Failover,
            "Logs" => Logs,
            "Advanced" => Advanced,
            "Diagnostics" => Diagnostics,
            _ => Dashboard
        };
        _ = RefreshCurrentAsync();
    }

    private void ToggleTheme()
    {
        _themeManager.Toggle();
        OnPropertyChanged(nameof(ThemeButtonText));
    }
}

public sealed class DashboardViewModel : PageViewModel
{
    public DashboardViewModel(PipeClient service) : base(service, "Dashboard", "Routing status and live network health.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ToggleRoutingCommand = new AsyncRelayCommand(_ => ToggleRoutingAsync());
    }

    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<PolicyRow> Policies { get; } = [];
    public ObservableCollection<LogRow> RecentEvents { get; } = [];
    private int _ruleCount;
    public int RuleCount { get => _ruleCount; private set => SetProperty(ref _ruleCount, value); }
    public DashboardStatus Status { get; private set; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleRoutingCommand { get; }
    public string RoutingButtonText => Status.RoutingEnabled ? "Disable routing" : "Enable routing";
    public string RoutingStatusText => Status.RoutingEnabled ? "Enabled" : "Disabled";

    public override Task RefreshAsync() => RunAsync(RefreshDataAsync);

    private async Task RefreshDataAsync()
    {
        var statusJson = await Service.CallAsync("GetStatus");
        Status = DashboardStatus.FromJson(statusJson);
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(RoutingButtonText));
        OnPropertyChanged(nameof(RoutingStatusText));
        await ReplaceAsync(Adapters, "GetAdapters", AdapterRow.FromJson);
        await ReplaceAsync(Policies, "GetPolicies", PolicyRow.FromJson);
        var rules = await Service.CallAsync("GetRules");
        RuleCount = JsonValue.Items(rules).Count();
        var interfaceNames = Adapters.GroupBy(adapter => adapter.Id.ToString("D"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var routePolicies = JsonValue.Items(JsonValue.Property(statusJson, "Policies"));
        foreach (var policy in Policies)
        {
            var route = routePolicies.FirstOrDefault(item => string.Equals(JsonValue.String(item, string.Empty, "PolicyId"), policy.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (route.ValueKind != JsonValueKind.Undefined)
            {
                var v4 = JsonValue.String(route, string.Empty, "ActiveIPv4Interface");
                var v6 = JsonValue.String(route, string.Empty, "ActiveIPv6Interface");
                policy.ActiveIPv4 = interfaceNames.TryGetValue(v4, out var v4Name) ? v4Name : string.IsNullOrWhiteSpace(v4) ? "Not selected" : v4;
                policy.ActiveIPv6 = interfaceNames.TryGetValue(v6, out var v6Name) ? v6Name : string.IsNullOrWhiteSpace(v6) ? "Not selected" : v6;
                policy.State = JsonValue.Bool(route, false, "NoHealthyIPv4Interface") || JsonValue.Bool(route, false, "NoHealthyIPv6Interface") ? "No healthy interface" : "Healthy";
            }
        }
        var logs = await Service.CallAsync("GetLogs");
        Replace(RecentEvents, JsonValue.Items(logs).Select(LogRow.FromJson).Take(8));
    }

    private async Task ToggleRoutingAsync() => await RunAsync(async () =>
    {
        await Service.CallAsync(Status.RoutingEnabled ? "DisableRouting" : "EnableRouting");
        await RefreshDataAsync();
        Message = Status.RoutingEnabled ? "Routing is enabled." : "Routing is disabled.";
    });

    private async Task ReplaceAsync<T>(ObservableCollection<T> target, string method, Func<JsonElement, T> map)
    {
        var payload = await Service.CallAsync(method);
        Replace(target, JsonValue.Items(payload).Select(map));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

public sealed class RulesViewModel : PageViewModel
{
    public RulesViewModel(PipeClient service) : base(service, "Application rules", "Choose a routing policy for each executable. Full executable paths take priority over process names.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        AddCommand = new RelayCommand(_ => BeginNewRule());
        AddRunningProcessCommand = new AsyncRelayCommand(_ => LoadRunningProcessesAsync(), _ => !IsBusy);
        UseProcessCommand = new RelayCommand(_ => BeginRuleForProcess(), _ => SelectedProcess is not null);
        CloseProcessPickerCommand = new RelayCommand(_ => IsProcessPickerOpen = false);
        EditCommand = new RelayCommand(parameter => BeginEdit(parameter as RuleRow));
        DeleteCommand = new AsyncRelayCommand(parameter => DeleteRuleAsync(parameter as RuleRow), parameter => parameter is RuleRow && !IsBusy);
        ToggleRuleCommand = new AsyncRelayCommand(parameter => ToggleRuleAsync(parameter as RuleRow), parameter => parameter is RuleRow && !IsBusy);
        SaveRuleCommand = new AsyncRelayCommand(_ => SaveRuleAsync(), _ => !IsBusy && IsEditing);
        CancelEditCommand = new RelayCommand(_ => { IsEditing = false; ErrorMessage = string.Empty; });
    }

    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<RuleRow> VisibleRules { get; } = [];
    public ObservableCollection<PolicyOption> PolicyOptions { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand AddCommand { get; }
    public AsyncRelayCommand AddRunningProcessCommand { get; }
    public RelayCommand UseProcessCommand { get; }
    public RelayCommand CloseProcessPickerCommand { get; }
    public RelayCommand EditCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ToggleRuleCommand { get; }
    public AsyncRelayCommand SaveRuleCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    private string _searchText = string.Empty;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ApplyFilter(); } }
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value)) SaveRuleCommand.RaiseCanExecuteChanged();
        }
    }
    private RuleDraft? _draft;
    public RuleDraft? Draft { get => _draft; private set => SetProperty(ref _draft, value); }
    public ObservableCollection<RunningProcessOption> RunningProcesses { get; } = [];
    private RunningProcessOption? _selectedProcess;
    public RunningProcessOption? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value)) UseProcessCommand.RaiseCanExecuteChanged();
        }
    }
    private bool _isProcessPickerOpen;
    public bool IsProcessPickerOpen { get => _isProcessPickerOpen; private set => SetProperty(ref _isProcessPickerOpen, value); }

    public override Task RefreshAsync() => RunAsync(RefreshDataAsync);

    private async Task RefreshDataAsync()
    {
        var policiesJson = await Service.CallAsync("GetPolicies");
        var policies = JsonValue.Items(policiesJson).Select(PolicyRow.FromJson).ToArray();
        PolicyOptions.Clear();
        foreach (var policy in policies)
        {
            PolicyOptions.Add(new PolicyOption { Id = policy.Id, Name = policy.Name });
        }

        var names = policies.ToDictionary(policy => policy.Id, policy => policy.Name);
        var rulesJson = await Service.CallAsync("GetRules");
        var adaptersJson = await Service.CallAsync("GetAdapters");
        var adapterNames = JsonValue.Items(adaptersJson).Select(AdapterRow.FromJson)
            .GroupBy(adapter => adapter.Id.ToString("D"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        var status = await Service.CallAsync("GetStatus");
        var routeStatuses = JsonValue.Items(JsonValue.Property(status, "Policies"))
            .ToDictionary(item => JsonValue.String(item, string.Empty, "PolicyId"), StringComparer.OrdinalIgnoreCase);
        Rules.Clear();
        foreach (var item in JsonValue.Items(rulesJson).OrderBy(item => JsonValue.Int32(item, 0, "Priority")))
        {
            var rule = RuleRow.FromJson(item, names);
            if (routeStatuses.TryGetValue(rule.PolicyId.ToString("D"), out var route))
            {
                var v4 = JsonValue.String(route, string.Empty, "ActiveIPv4Interface");
                var v6 = JsonValue.String(route, string.Empty, "ActiveIPv6Interface");
                rule.IPv4Route = adapterNames.TryGetValue(v4, out var v4Name) ? v4Name : string.IsNullOrWhiteSpace(v4) ? "Not selected" : v4;
                rule.IPv6Route = adapterNames.TryGetValue(v6, out var v6Name) ? v6Name : string.IsNullOrWhiteSpace(v6) ? "Not selected" : v6;
            }
            Rules.Add(rule);
        }

        ApplyFilter();
    }

    private void BeginNewRule()
    {
        Draft = new RuleDraft { PolicyId = PolicyOptions.FirstOrDefault()?.Id ?? Guid.Empty };
        IsProcessPickerOpen = false;
        IsEditing = true;
        ErrorMessage = string.Empty;
    }

    private void BeginEdit(RuleRow? rule)
    {
        if (rule is null)
        {
            return;
        }

        var draft = new RuleDraft();
        draft.CopyFrom(rule);
        Draft = draft;
        IsEditing = true;
        ErrorMessage = string.Empty;
    }

    private async Task LoadRunningProcessesAsync() => await RunAsync(async () =>
    {
        var response = await Service.CallAsync("GetProcesses");
        RunningProcesses.Clear();
        foreach (var process in JsonValue.Items(response).Select(RunningProcessOption.FromJson))
        {
            RunningProcesses.Add(process);
        }
        SelectedProcess = RunningProcesses.FirstOrDefault();
        IsEditing = false;
        IsProcessPickerOpen = true;
        Message = RunningProcesses.Count == 0 ? "No running applications were found." : $"Found {RunningProcesses.Count} running applications.";
    });

    private void BeginRuleForProcess()
    {
        if (SelectedProcess is null)
        {
            return;
        }

        BeginNewRule();
        Draft!.ProcessName = SelectedProcess.ProcessName;
        Draft.DisplayName = SelectedProcess.ProcessName;
        Draft.ExecutablePath = SelectedProcess.ExecutablePath;
        IsProcessPickerOpen = false;
    }

    public void SetExecutablePath(string path)
    {
        if (Draft is null)
        {
            return;
        }

        Draft.ExecutablePath = path;
        Draft.ProcessName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(Draft.DisplayName))
        {
            Draft.DisplayName = Path.GetFileNameWithoutExtension(path);
        }
    }

    private async Task SaveRuleAsync() => await RunAsync(async () =>
    {
        if (Draft is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Draft.ExecutablePath) && string.IsNullOrWhiteSpace(Draft.ProcessName))
        {
            ErrorMessage = "Choose an executable or enter a process name.";
            return;
        }

        if (Draft.PolicyId == Guid.Empty)
        {
            ErrorMessage = "Choose a routing policy.";
            return;
        }

        var wire = new
        {
            Id = Draft.Id.ToString("D"),
            DisplayName = string.IsNullOrWhiteSpace(Draft.DisplayName) ? Draft.ProcessName : Draft.DisplayName,
            ExecutablePath = string.IsNullOrWhiteSpace(Draft.ExecutablePath) ? null : Draft.ExecutablePath,
            ProcessName = string.IsNullOrWhiteSpace(Draft.ProcessName) ? null : Draft.ProcessName,
            Draft.Enabled,
            PolicyId = Draft.PolicyId.ToString("D"),
            Draft.Priority,
            CreatedAt = Draft.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await Service.CallAsync("SaveRule", wire);
        IsEditing = false;
        Message = "Rule saved.";
        await RefreshDataAsync();
    });

    private async Task DeleteRuleAsync(RuleRow? rule)
    {
        if (rule is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Service.CallAsync("DeleteRule", new { Id = rule.Id.ToString("D") });
            Message = $"Removed {rule.DisplayName}.";
            await RefreshDataAsync();
        });
    }

    private async Task ToggleRuleAsync(RuleRow? rule)
    {
        if (rule is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Service.CallAsync("SaveRule", new
            {
                Id = rule.Id.ToString("D"),
                rule.DisplayName,
                ExecutablePath = string.IsNullOrWhiteSpace(rule.ExecutablePath) ? null : rule.ExecutablePath,
                ProcessName = string.IsNullOrWhiteSpace(rule.ProcessName) ? null : rule.ProcessName,
                Enabled = !rule.Enabled,
                PolicyId = rule.PolicyId.ToString("D"),
                rule.Priority,
                CreatedAt = rule.CreatedAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await RefreshDataAsync();
            Message = rule.Enabled ? "Rule disabled." : "Rule enabled.";
        });
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var matches = Rules.Where(rule => string.IsNullOrEmpty(query)
            || rule.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || rule.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)
            || rule.PolicyName.Contains(query, StringComparison.OrdinalIgnoreCase));
        VisibleRules.Clear();
        foreach (var item in matches)
        {
            VisibleRules.Add(item);
        }
    }
}

public sealed class InterfacesViewModel : PageViewModel
{
    public InterfacesViewModel(PipeClient service) : base(service, "Interfaces", "View physical and virtual adapters, address families, health, and link details.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ToggleUsabilityCommand = new AsyncRelayCommand(parameter => ToggleUsabilityAsync(parameter as AdapterRow), parameter => parameter is AdapterRow && !IsBusy);
        TestSelectedCommand = new AsyncRelayCommand(parameter => TestSelectedAsync(parameter?.ToString()), _ => !IsBusy && SelectedAdapter is not null);
        ToggleVirtualCommand = new AsyncRelayCommand(_ => ToggleVirtualAsync(), _ => !IsBusy);
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShowVirtualInterfaces))
            {
                UpdateVisibleAdapters();
            }
        };
    }

    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<AdapterRow> VisibleAdapters { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleUsabilityCommand { get; }
    public AsyncRelayCommand TestSelectedCommand { get; }
    public AsyncRelayCommand ToggleVirtualCommand { get; }
    private AdapterRow? _selectedAdapter;
    public AdapterRow? SelectedAdapter { get => _selectedAdapter; set { if (SetProperty(ref _selectedAdapter, value)) TestSelectedCommand.RaiseCanExecuteChanged(); } }
    private bool _showVirtualInterfaces;
    public bool ShowVirtualInterfaces { get => _showVirtualInterfaces; set => SetProperty(ref _showVirtualInterfaces, value); }
    public string TestResult { get; private set; } = string.Empty;

    public override Task RefreshAsync() => RunAsync(RefreshDataAsync);

    private async Task RefreshDataAsync()
    {
        var settings = await Service.CallAsync("GetSettings");
        ShowVirtualInterfaces = JsonValue.Bool(settings, false, "ShowVirtualInterfaces");
        await LoadAdaptersAsync();
    }

    private async Task LoadAdaptersAsync()
    {
        var payload = await Service.CallAsync("GetAdapters");
        var selectedId = SelectedAdapter?.Id ?? Guid.Empty;
        Adapters.Clear();
        SelectedAdapter = null;
        foreach (var item in JsonValue.Items(payload).Select(AdapterRow.FromJson))
        {
            Adapters.Add(item);
            if (item.Id == selectedId)
            {
                SelectedAdapter = item;
            }
        }

        SelectedAdapter ??= Adapters.FirstOrDefault();
        UpdateVisibleAdapters();
    }

    private void UpdateVisibleAdapters()
    {
        VisibleAdapters.Clear();
        foreach (var adapter in Adapters.Where(adapter => ShowVirtualInterfaces || !adapter.IsVirtual))
        {
            VisibleAdapters.Add(adapter);
        }
    }

    private async Task ToggleUsabilityAsync(AdapterRow? adapter)
    {
        if (adapter is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var newValue = !adapter.IsUserAllowed;
            await Service.CallAsync("SetInterfaceUsability", new { InterfaceId = adapter.Id.ToString("D"), Allowed = newValue });
            adapter.IsUserAllowed = newValue;
            Message = newValue ? $"{adapter.Name} can be used for routing." : $"{adapter.Name} is excluded from routing.";
        });
    }

    private async Task TestSelectedAsync(string? family)
    {
        if (SelectedAdapter is null)
        {
            return;
        }

        var families = family == "Both" ? new[] { "IPv4", "IPv6" } : new[] { family is "IPv4" or "IPv6" ? family : "IPv4" };
        await RunAsync(async () =>
        {
            var results = new List<string>();
            foreach (var item in families)
            {
                try
                {
                    var response = await Service.CallAsync("TestInterface", new { InterfaceId = SelectedAdapter.Id.ToString("D"), Family = item });
                    var health = JsonValue.String(response, "Unknown", "Health");
                    var latency = JsonValue.Double(response, -1, "LatencyMs");
                    results.Add(latency >= 0 ? $"{item}: {health} ({latency:0} ms)" : $"{item}: {health}");
                }
                catch (Exception ex)
                {
                    results.Add($"{item}: failed — {ex.Message}");
                }
            }

            TestResult = string.Join("   ·   ", results);
            OnPropertyChanged(nameof(TestResult));
        });
    }

    private async Task ToggleVirtualAsync() => await RunAsync(async () =>
    {
        var settings = await GetSettingsNodeForEditAsync(Service);
        Set(settings, "ShowVirtualInterfaces", ShowVirtualInterfaces);
        await Service.CallAsync("SaveSettings", settings);
        Message = ShowVirtualInterfaces ? "Virtual interfaces are shown." : "Virtual interfaces are hidden.";
    });

    internal static async Task<JsonObject> GetSettingsNodeForEditAsync(PipeClient service)
    {
        var settings = await service.CallAsync("GetSettings");
        return JsonNode.Parse(settings.GetRawText()) as JsonObject ?? new JsonObject();
    }

    internal static void Set(JsonObject json, string name, JsonNode? value)
    {
        var matchingKey = json.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        json[matchingKey ?? char.ToLowerInvariant(name[0]) + name[1..]] = value?.DeepClone();
    }

    internal static void Set(JsonObject json, string name, bool value) => Set(json, name, JsonNodeValue.Create(value));
    internal static void Set(JsonObject json, string name, string? value) => Set(json, name, value is null ? null : JsonNodeValue.Create(value));
}

public sealed class FailoverViewModel : PageViewModel
{
    public FailoverViewModel(PipeClient service) : base(service, "Failover", "Tune independent IPv4 and IPv6 health checks, recovery, and policy fallback behavior.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !IsBusy && SelectedPolicy is not null);
        AddPolicyCommand = new RelayCommand(_ => AddPolicy());
        DeletePolicyCommand = new AsyncRelayCommand(_ => DeletePolicyAsync(), _ => !IsBusy && SelectedPolicy is not null && SelectedPolicy.Id != DefaultPolicyId);
        SetDefaultPolicyCommand = new AsyncRelayCommand(_ => SetDefaultPolicyAsync(), _ => !IsBusy && SelectedPolicy is not null && SelectedPolicy.Id != DefaultPolicyId);
        TestAllCommand = new AsyncRelayCommand(_ => TestAllAsync(), _ => !IsBusy);
        AddEndpointCommand = new RelayCommand(_ => AddEndpoint());
        RemoveEndpointCommand = new RelayCommand(_ => RemoveEndpoint(), _ => SelectedEndpoint is not null);
        SaveEndpointsCommand = new AsyncRelayCommand(_ => SaveEndpointsAsync(), _ => !IsBusy);
    }

    public ObservableCollection<PolicyRow> Policies { get; } = [];
    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<EndpointRow> Endpoints { get; } = [];
    public ObservableCollection<InterfaceOption> InterfaceOptions { get; } = [];
    public ObservableCollection<InterfaceOption> FallbackOptions { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand AddPolicyCommand { get; }
    public AsyncRelayCommand DeletePolicyCommand { get; }
    public AsyncRelayCommand SetDefaultPolicyCommand { get; }
    public AsyncRelayCommand TestAllCommand { get; }
    public RelayCommand AddEndpointCommand { get; }
    public RelayCommand RemoveEndpointCommand { get; }
    public AsyncRelayCommand SaveEndpointsCommand { get; }
    private PolicyRow? _selectedPolicy;
    public PolicyRow? SelectedPolicy
    {
        get => _selectedPolicy;
        set
        {
            if (SetProperty(ref _selectedPolicy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                DeletePolicyCommand.RaiseCanExecuteChanged();
                SetDefaultPolicyCommand.RaiseCanExecuteChanged();
            }
        }
    }
    private EndpointRow? _selectedEndpoint;
    public EndpointRow? SelectedEndpoint { get => _selectedEndpoint; set { if (SetProperty(ref _selectedEndpoint, value)) RemoveEndpointCommand.RaiseCanExecuteChanged(); } }
    public string TestSummary { get; private set; } = "";
    public Guid DefaultPolicyId { get; private set; }
    private int _healthyProbeInterval = 20;
    public int HealthyProbeInterval { get => _healthyProbeInterval; set => SetProperty(ref _healthyProbeInterval, value); }
    private int _suspectProbeInterval = 2;
    public int SuspectProbeInterval { get => _suspectProbeInterval; set => SetProperty(ref _suspectProbeInterval, value); }
    private int _downProbeInterval = 8;
    public int DownProbeInterval { get => _downProbeInterval; set => SetProperty(ref _downProbeInterval, value); }
    private int _probeTimeout = 3;
    public int ProbeTimeout { get => _probeTimeout; set => SetProperty(ref _probeTimeout, value); }

    public override Task RefreshAsync() => RunAsync(RefreshDataAsync);

    private async Task RefreshDataAsync()
    {
        var selectedPolicyId = SelectedPolicy?.Id ?? Guid.Empty;
        var policies = await Service.CallAsync("GetPolicies");
        Policies.Clear();
        foreach (var policy in JsonValue.Items(policies).Select(PolicyRow.FromJson))
        {
            Policies.Add(policy);
        }

        SelectedPolicy = Policies.FirstOrDefault(policy => policy.Id == selectedPolicyId) ?? Policies.FirstOrDefault();

        var adapters = await Service.CallAsync("GetAdapters");
        Adapters.Clear();
        foreach (var adapter in JsonValue.Items(adapters).Select(AdapterRow.FromJson))
        {
            Adapters.Add(adapter);
        }
        InterfaceOptions.Clear();
        foreach (var option in Adapters.Select(adapter => new InterfaceOption { Id = adapter.Id, Name = adapter.Name }))
        {
            InterfaceOptions.Add(option);
        }
        FallbackOptions.Clear();
        FallbackOptions.Add(new InterfaceOption { Id = null, Name = "No fallback" });
        foreach (var option in InterfaceOptions)
        {
            FallbackOptions.Add(option);
        }

        var settings = await Service.CallAsync("GetSettings");
        DefaultPolicyId = JsonValue.Guid(JsonValue.Property(settings, "DefaultPolicy"), "Id");
        DeletePolicyCommand.RaiseCanExecuteChanged();
        SetDefaultPolicyCommand.RaiseCanExecuteChanged();
        HealthyProbeInterval = JsonValue.Seconds(settings, 20, "HealthyProbeInterval");
        SuspectProbeInterval = JsonValue.Seconds(settings, 2, "SuspectProbeInterval");
        DownProbeInterval = JsonValue.Seconds(settings, 8, "DownProbeInterval");
        ProbeTimeout = JsonValue.Seconds(settings, 3, "ProbeTimeout");
        Endpoints.Clear();
        foreach (var endpoint in JsonValue.Items(JsonValue.Property(settings, "ProbeEndpoints")).Select(EndpointRow.FromJson))
        {
            Endpoints.Add(endpoint);
        }
    }

    private async Task SaveAsync() => await RunAsync(async () =>
    {
        if (SelectedPolicy is null)
        {
            return;
        }

        await Service.CallAsync("SavePolicy", SelectedPolicy.ToWire());
        Message = "Failover policy saved.";
        await RefreshDataAsync();
    });

    private void AddPolicy()
    {
        var primaryId = Adapters.FirstOrDefault(adapter => adapter.IsUserAllowed)?.Id ?? Guid.Empty;
        var fallbackId = Adapters.FirstOrDefault(adapter => adapter.IsUserAllowed && adapter.Id != primaryId)?.Id;
        var policy = new PolicyRow
        {
            Id = Guid.NewGuid(),
            Name = "New policy",
            PrimaryInterfaceId = primaryId,
            FallbackInterfaceId = fallbackId,
            FailoverEnabled = true,
            AutoFailback = true,
            Enabled = true,
            OrderedInterfaceCandidates = []
        };
        Policies.Add(policy);
        SelectedPolicy = policy;
    }

    private async Task DeletePolicyAsync() => await RunAsync(async () =>
    {
        if (SelectedPolicy is null)
        {
            return;
        }

        await Service.CallAsync("DeletePolicy", new { Id = SelectedPolicy.Id.ToString("D") });
        Message = "Policy deleted.";
        SelectedPolicy = null;
        await RefreshDataAsync();
    });

    private async Task SetDefaultPolicyAsync() => await RunAsync(async () =>
    {
        if (SelectedPolicy is null)
        {
            return;
        }

        await Service.CallAsync("SetDefaultPolicy", new { Id = SelectedPolicy.Id.ToString("D") });
        Message = "Default routing policy updated.";
        await RefreshDataAsync();
    });

    private void AddEndpoint()
    {
        var endpoint = new EndpointRow { Id = Guid.NewGuid(), Name = "New endpoint", Url = "https://" };
        Endpoints.Add(endpoint);
        SelectedEndpoint = endpoint;
    }

    private void RemoveEndpoint()
    {
        if (SelectedEndpoint is null)
        {
            return;
        }

        Endpoints.Remove(SelectedEndpoint);
        SelectedEndpoint = null;
    }

    private async Task SaveEndpointsAsync() => await RunAsync(async () =>
    {
        var settings = await InterfacesViewModel.GetSettingsNodeForEditAsync(Service);
        InterfacesViewModel.Set(settings, "ProbeEndpoints", JsonSerializer.SerializeToNode(Endpoints.Select(endpoint => endpoint.ToWire()).ToArray()));
        InterfacesViewModel.Set(settings, "HealthyProbeInterval", JsonSerializer.SerializeToNode(TimeSpan.FromSeconds(Math.Max(5, HealthyProbeInterval))));
        InterfacesViewModel.Set(settings, "SuspectProbeInterval", JsonSerializer.SerializeToNode(TimeSpan.FromSeconds(Math.Max(1, SuspectProbeInterval))));
        InterfacesViewModel.Set(settings, "DownProbeInterval", JsonSerializer.SerializeToNode(TimeSpan.FromSeconds(Math.Max(2, DownProbeInterval))));
        InterfacesViewModel.Set(settings, "ProbeTimeout", JsonSerializer.SerializeToNode(TimeSpan.FromSeconds(Math.Clamp(ProbeTimeout, 1, 30))));
        await Service.CallAsync("SaveSettings", settings);
        Message = "Probe timing and endpoints saved.";
    });

    private async Task TestAllAsync() => await RunAsync(async () =>
    {
        var completed = 0;
        var healthy = 0;
        var failures = 0;
        var details = new List<string>();
        foreach (var adapter in Adapters.Where(adapter => adapter.IsUserAllowed))
        {
            foreach (var family in new[] { "IPv4", "IPv6" })
            {
                try
                {
                    var result = await Service.CallAsync("TestInterface", new { InterfaceId = adapter.Id.ToString("D"), Family = family });
                    var state = JsonValue.String(result, "Unknown", "Health");
                    var latency = JsonValue.Double(result, -1, "LatencyMs");
                    completed++;
                    if (state.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                    {
                        healthy++;
                    }

                    if (details.Count < 6)
                    {
                        details.Add(latency >= 0
                            ? $"{adapter.Name} {family}: {state} ({latency:0} ms)"
                            : $"{adapter.Name} {family}: {state}");
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    if (details.Count < 6)
                    {
                        details.Add($"{adapter.Name} {family}: {ex.Message}");
                    }
                }
            }
        }

        TestSummary = $"Completed {completed}; healthy {healthy}; non-healthy {completed - healthy}; errors {failures}. " + string.Join(" · ", details);
        OnPropertyChanged(nameof(TestSummary));
    });
}

public sealed class LogsViewModel : PageViewModel
{
    public LogsViewModel(PipeClient service) : base(service, "Logs", "Recent service, sing-box, adapter, and failover events.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        PauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
        ClearCommand = new RelayCommand(_ => Entries.Clear());
        CopyCommand = new RelayCommand(_ => CopyVisibleLogs());
        OpenDirectoryCommand = new RelayCommand(_ => OpenLogDirectory());
    }

    public ObservableCollection<LogRow> Entries { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenDirectoryCommand { get; }
    private bool _isPaused;
    public bool IsPaused { get => _isPaused; set { if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PauseButtonText)); } }
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    public override Task RefreshAsync() => IsPaused ? Task.CompletedTask : RunAsync(async () =>
    {
        var logs = await Service.CallAsync("GetLogs");
        Entries.Clear();
        foreach (var entry in JsonValue.Items(logs).Select(LogRow.FromJson))
        {
            Entries.Add(entry);
        }
    });

    private void CopyVisibleLogs()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Entries.Select(entry => $"{entry.Timestamp} [{entry.Level}] {entry.Source}: {entry.Message}")));
            Message = "Visible logs copied.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not copy logs: {ex.Message}";
        }
    }

    private void OpenLogDirectory()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyBalance", "logs");
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                ErrorMessage = "The service log directory is not available yet.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open the log directory: {ex.Message}";
        }
    }
}

public sealed class AdvancedViewModel : PageViewModel
{
    private JsonObject? _settings;
    public AdvancedViewModel(PipeClient service) : base(service, "Advanced", "Service-owned routing settings and sing-box lifecycle actions.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SaveCommand = new AsyncRelayCommand(_ => SaveSettingsAsync(), _ => !IsBusy);
        ToggleRoutingCommand = new AsyncRelayCommand(_ => ToggleRoutingAsync(), _ => !IsBusy);
        ValidateCommand = new AsyncRelayCommand(_ => ValidateConfigAsync(), _ => !IsBusy);
        RestartCommand = new AsyncRelayCommand(_ => RestartCoreAsync(), _ => !IsBusy);
        ShowConfigCommand = new AsyncRelayCommand(_ => ShowConfigAsync(), _ => !IsBusy);
        ExportDiagnosticsCommand = new AsyncRelayCommand(_ => ExportDiagnosticsAsync(), _ => !IsBusy);
        OpenDataDirectoryCommand = new RelayCommand(_ => OpenDataDirectory());
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ToggleRoutingCommand { get; }
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand RestartCommand { get; }
    public AsyncRelayCommand ShowConfigCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public RelayCommand OpenDataDirectoryCommand { get; }
    private bool _routingEnabled;
    public bool RoutingEnabled { get => _routingEnabled; set => SetProperty(ref _routingEnabled, value); }
    private bool _ipv6Enabled = true;
    public bool IPv6Enabled { get => _ipv6Enabled; set => SetProperty(ref _ipv6Enabled, value); }
    private bool _strictRoute;
    public bool StrictRoute { get => _strictRoute; set => SetProperty(ref _strictRoute, value); }
    private bool _disconnectOldConnections;
    public bool DisconnectOldConnections { get => _disconnectOldConnections; set => SetProperty(ref _disconnectOldConnections, value); }
    private bool _showVirtualInterfaces;
    public bool ShowVirtualInterfaces { get => _showVirtualInterfaces; set => SetProperty(ref _showVirtualInterfaces, value); }
    private string _singBoxPath = string.Empty;
    public string SingBoxPath { get => _singBoxPath; set => SetProperty(ref _singBoxPath, value); }
    private string _detectedVersion = "—";
    public string DetectedVersion { get => _detectedVersion; private set => SetProperty(ref _detectedVersion, value); }
    private string _generatedConfig = string.Empty;
    public string GeneratedConfig { get => _generatedConfig; private set => SetProperty(ref _generatedConfig, value); }
    public string ToggleRoutingText => RoutingEnabled ? "Disable routing" : "Enable routing";
    private bool _redactIpAddresses = true;
    public bool RedactIpAddresses { get => _redactIpAddresses; set => SetProperty(ref _redactIpAddresses, value); }

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var settings = await Service.CallAsync("GetSettings");
        _settings = JsonNode.Parse(settings.GetRawText()) as JsonObject ?? new JsonObject();
        RoutingEnabled = JsonValue.Bool(settings, false, "Enabled");
        IPv6Enabled = JsonValue.Bool(settings, true, "Ipv6Enabled", "IPv6Enabled");
        StrictRoute = JsonValue.Bool(settings, false, "StrictRoute");
        DisconnectOldConnections = JsonValue.Bool(settings, false, "DisconnectOldConnectionsOnFailover");
        ShowVirtualInterfaces = JsonValue.Bool(settings, false, "ShowVirtualInterfaces");
        SingBoxPath = JsonValue.String(settings, string.Empty, "SingBoxPath");
        var status = await Service.CallAsync("GetStatus");
        DetectedVersion = JsonValue.String(status, "—", "SingBoxVersion", "CoreVersion", "Version");
        OnPropertyChanged(nameof(ToggleRoutingText));
    });

    public void ChooseSingBoxPath(string path) => SingBoxPath = path;

    private async Task SaveSettingsAsync() => await RunAsync(PersistSettingsAsync);

    private async Task PersistSettingsAsync()
    {
        var settings = await GetSettingsCopyAsync();
        InterfacesViewModel.Set(settings, "Enabled", RoutingEnabled);
        InterfacesViewModel.Set(settings, "Ipv6Enabled", IPv6Enabled);
        InterfacesViewModel.Set(settings, "StrictRoute", StrictRoute);
        InterfacesViewModel.Set(settings, "DisconnectOldConnectionsOnFailover", DisconnectOldConnections);
        InterfacesViewModel.Set(settings, "ShowVirtualInterfaces", ShowVirtualInterfaces);
        InterfacesViewModel.Set(settings, "SingBoxPath", string.IsNullOrWhiteSpace(SingBoxPath) ? null : SingBoxPath);
        await Service.CallAsync("SaveSettings", settings);
        _settings = (JsonObject)settings.DeepClone();
        Message = "Advanced settings saved.";
    }

    private async Task ToggleRoutingAsync() => await RunAsync(async () =>
    {
        await Service.CallAsync(RoutingEnabled ? "DisableRouting" : "EnableRouting");
        RoutingEnabled = !RoutingEnabled;
        OnPropertyChanged(nameof(ToggleRoutingText));
        if (_settings is not null)
        {
            InterfacesViewModel.Set(_settings, "Enabled", RoutingEnabled);
        }
        Message = RoutingEnabled ? "Routing is enabled." : "Routing is disabled.";
    });

    private async Task ValidateConfigAsync() => await RunAsync(async () =>
    {
        var response = await Service.CallAsync("ValidateConfig");
        Message = response.ValueKind == JsonValueKind.Null
            ? "Configuration is valid."
            : JsonValue.String(response, response.GetRawText(), "Message", "Result", "Status");
    });

    private async Task RestartCoreAsync() => await RunAsync(async () =>
    {
        var response = await Service.CallAsync("RestartCore");
        Message = JsonValue.String(response, "Core restart request completed.", "Message", "Status");
    });

    private async Task ShowConfigAsync() => await RunAsync(async () =>
    {
        var response = await Service.CallAsync("GetGeneratedConfig");
        GeneratedConfig = JsonValue.String(response, response.GetRawText(), "Json", "Config", "Text");
    });

    private async Task ExportDiagnosticsAsync() => await RunAsync(async () =>
    {
        var response = await Service.CallAsync("ExportDiagnostics", new { RedactIpAddresses });
        var fileName = JsonValue.String(response, "EasyBalance-diagnostics.zip", "FileName");
        var base64 = JsonValue.String(response, string.Empty, "DataBase64");
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidDataException("The service returned an empty diagnostics archive.");
        }

        byte[] archive;
        try
        {
            archive = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The service returned an invalid diagnostics archive.", exception);
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save EasyBalance diagnostics",
            FileName = Path.GetFileName(fileName),
            DefaultExt = ".zip",
            AddExtension = true,
            Filter = "ZIP archive (*.zip)|*.zip|All files (*.*)|*.*",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
        {
            Message = "Diagnostics export canceled.";
            return;
        }

        await File.WriteAllBytesAsync(dialog.FileName, archive);
        Message = $"Diagnostics saved to {dialog.FileName}";
    });

    private async Task<JsonObject> GetSettingsCopyAsync()
    {
        if (_settings is null)
        {
            var settings = await Service.CallAsync("GetSettings");
            _settings = JsonNode.Parse(settings.GetRawText()) as JsonObject ?? new JsonObject();
        }

        return (JsonObject)_settings.DeepClone();
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenDataDirectory()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EasyBalance");
        try
        {
            Directory.CreateDirectory(path);
            OpenPath(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open the data directory: {ex.Message}";
        }
    }

}

public sealed class DiagnosticsViewModel : PageViewModel
{
    private TimeSpan? _lastCpuTime;
    private DateTimeOffset? _lastSample;
    public DiagnosticsViewModel(PipeClient service) : base(service, "Diagnostics", "Local service, sing-box, and UI resource information. No telemetry is sent.")
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
    }

    public ObservableCollection<DiagnosticLine> Metrics { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    private string _diagnosticsJson = "{}";
    public string DiagnosticsJson { get => _diagnosticsJson; private set => SetProperty(ref _diagnosticsJson, value); }

    public override Task RefreshAsync() => RunAsync(async () =>
    {
        var payload = await Service.CallAsync("GetDiagnostics");
        DiagnosticsJson = payload.ValueKind == JsonValueKind.Object ? JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) : payload.GetRawText();
        Metrics.Clear();
        AddMetric(payload, "Service CPU", "ServiceCpuPercent", "ServiceCpu", "CpuPercent");
        AddMetric(payload, "Service private memory", "ServicePrivateMemoryBytes", "ServicePrivateBytes", "PrivateMemoryBytes");
        AddMetric(payload, "Service working set", "ServiceWorkingSetBytes", "WorkingSetBytes");
        AddMetric(payload, "Service CPU time", "ServiceCpuSeconds");
        AddMetric(payload, "sing-box private memory", "SingBoxPrivateBytes");
        AddMetric(payload, "sing-box working set", "SingBoxWorkingSetBytes");
        AddMetric(payload, "sing-box CPU time", "SingBoxCpuSeconds");
        AddMetric(payload, "Reported UI private memory", "UiPrivateBytes");
        AddMetric(payload, "Reported UI working set", "UiWorkingSetBytes");
        AddMetric(payload, "Reported UI CPU time", "UiCpuSeconds");
        AddMetric(payload, "Core memory", "SingBoxMemoryBytes", "CoreMemoryBytes");
        AddMetric(payload, "Application rules", "RulesCount", "ApplicationRuleCount");
        AddMetric(payload, "Policies", "PoliciesCount", "PolicyCount");
        AddMetric(payload, "Generated route rules", "GeneratedRouteRules");
        AddMetric(payload, "Generated selectors", "GeneratedSelectors");
        AddMetric(payload, "Direct outbounds", "GeneratedDirectOutbounds");
        AddMetric(payload, "Health probes", "HealthProbes");
        AddMetric(payload, "Successful probes", "SuccessfulProbes");
        AddMetric(payload, "Failed probes", "FailedProbes");
        AddMetric(payload, "Failovers", "FailoverCount");
        AddMetric(payload, "Failbacks", "FailbackCount");
        AddMetric(payload, "Health contexts", "HealthContexts");
        AddMetric(payload, "Core running", "CoreRunning");
        AddMetric(payload, "Routing enabled", "RoutingEnabled");
        AddUiMetrics();
    });

    private void AddMetric(JsonElement source, string label, params string[] names)
    {
        var value = JsonValue.Property(source, names);
        if (value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            Metrics.Add(new DiagnosticLine(label, FormatMetric(label, value)));
        }
    }

    private static string FormatMetric(string label, JsonElement value)
    {
        if (label.Contains("memory", StringComparison.OrdinalIgnoreCase) || label.Contains("working set", StringComparison.OrdinalIgnoreCase))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes))
            {
                return $"{bytes / 1024d / 1024d:0.0} MB";
            }
        }

        if (label.Contains("CPU time", StringComparison.OrdinalIgnoreCase)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var cpuSeconds))
        {
            return $"{cpuSeconds:0.0} s";
        }

        return value.ToString();
    }

    private void AddUiMetrics()
    {
        using var process = Process.GetCurrentProcess();
        var currentSample = DateTimeOffset.UtcNow;
        var currentCpu = process.TotalProcessorTime;
        var cpuText = "Sampling…";
        if (_lastCpuTime is { } previousCpu && _lastSample is { } previousSample)
        {
            var elapsed = (currentSample - previousSample).TotalMilliseconds * Environment.ProcessorCount;
            if (elapsed > 0)
            {
                cpuText = $"{Math.Clamp((currentCpu - previousCpu).TotalMilliseconds / elapsed * 100, 0, 100):0.0}%";
            }
        }

        _lastCpuTime = currentCpu;
        _lastSample = currentSample;
        Metrics.Add(new DiagnosticLine("UI CPU", cpuText));
        Metrics.Add(new DiagnosticLine("UI private memory", $"{process.PrivateMemorySize64 / 1024d / 1024d:0.0} MB"));
        Metrics.Add(new DiagnosticLine("UI working set", $"{process.WorkingSet64 / 1024d / 1024d:0.0} MB"));
    }
}
