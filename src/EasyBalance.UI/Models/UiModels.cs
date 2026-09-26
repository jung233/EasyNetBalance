using System.Collections.ObjectModel;
using System.IO;
using EasyBalance.UI.Core;
using System.Text.Json;

namespace EasyBalance.UI.Models;

public sealed class AdapterRow : ObservableObject
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Unknown interface";
    public string Description { get; init; } = string.Empty;
    public string Type { get; init; } = "—";
    public string OperationalStatus { get; init; } = "Unknown";
    public string SpeedText { get; init; } = "—";
    public string IPv4Addresses { get; init; } = "—";
    public string IPv6Addresses { get; init; } = "—";
    public string IPv4Gateways { get; init; } = "—";
    public string IPv6Gateways { get; init; } = "—";
    public string IPv4Health { get; init; } = "Unknown";
    public string IPv6Health { get; init; } = "Unknown";
    public string IPv4LatencyText { get; init; } = "—";
    public string IPv6LatencyText { get; init; } = "—";
    public string LastProbeText { get; init; } = "—";
    public string LastStateChangeText { get; init; } = "—";
    public bool IsVirtual { get; init; }
    private bool _isUserAllowed;
    public bool IsUserAllowed
    {
        get => _isUserAllowed;
        set
        {
            if (SetProperty(ref _isUserAllowed, value))
            {
                OnPropertyChanged(nameof(UseActionText));
            }
        }
    }
    public string UseActionText => IsUserAllowed ? "Exclude" : "Allow";

    public static AdapterRow FromJson(JsonElement item)
    {
        var speed = JsonValue.Int64(item, 0, "Speed");
        return new AdapterRow
        {
            Id = JsonValue.Guid(item, "Id"),
            Name = JsonValue.String(item, "Unknown interface", "Name", "FriendlyName"),
            Description = JsonValue.String(item, string.Empty, "Description"),
            Type = JsonValue.String(item, "Other", "NetworkInterfaceType", "Type"),
            OperationalStatus = JsonValue.String(item, "Unknown", "OperationalStatus", "LinkState"),
            SpeedText = speed <= 0 ? "—" : speed >= 1_000_000_000 ? $"{speed / 1_000_000_000d:0.#} Gbps" : $"{speed / 1_000_000d:0.#} Mbps",
            IPv4Addresses = DisplayOrDash(JsonValue.StringList(item, "IPv4Addresses")),
            IPv6Addresses = DisplayOrDash(JsonValue.StringList(item, "IPv6Addresses")),
            IPv4Gateways = DisplayOrDash(JsonValue.StringList(item, "IPv4Gateways")),
            IPv6Gateways = DisplayOrDash(JsonValue.StringList(item, "IPv6Gateways")),
            IPv4Health = JsonValue.String(item, "Unknown", "IPv4Health"),
            IPv6Health = JsonValue.String(item, "Unknown", "IPv6Health"),
            IPv4LatencyText = FormatLatency(JsonValue.Milliseconds(item, "IPv4Latency")),
            IPv6LatencyText = FormatLatency(JsonValue.Milliseconds(item, "IPv6Latency")),
            LastProbeText = FormatTime(JsonValue.DateTimeOffset(item, "LastProbeTime")),
            LastStateChangeText = FormatTime(JsonValue.DateTimeOffset(item, "LastStateChange")),
            IsVirtual = JsonValue.Bool(item, false, "IsVirtual"),
            IsUserAllowed = JsonValue.Bool(item, true, "IsUserAllowed")
        };
    }

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string FormatLatency(double milliseconds) => milliseconds < 0 ? "—" : $"{milliseconds:0} ms";
    private static string FormatTime(DateTimeOffset? value) => value is null ? "—" : value.Value.ToLocalTime().ToString("g");
}

public sealed class PolicyOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Policy";
    public override string ToString() => Name;
}

public sealed class InterfaceOption
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = "Interface";
    public override string ToString() => Name;
}

public sealed class PolicyRow : ObservableObject
{
    public Guid Id { get; init; }
    private string _name = "Policy";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    private Guid _primaryInterfaceId;
    public Guid PrimaryInterfaceId { get => _primaryInterfaceId; set => SetProperty(ref _primaryInterfaceId, value); }
    private Guid? _fallbackInterfaceId;
    public Guid? FallbackInterfaceId { get => _fallbackInterfaceId; set => SetProperty(ref _fallbackInterfaceId, value); }
    private bool _failoverEnabled;
    public bool FailoverEnabled { get => _failoverEnabled; set => SetProperty(ref _failoverEnabled, value); }
    private bool _autoFailback;
    public bool AutoFailback { get => _autoFailback; set => SetProperty(ref _autoFailback, value); }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    private int _failureThreshold = 3;
    public int FailureThreshold { get => _failureThreshold; set => SetProperty(ref _failureThreshold, value); }
    private int _recoveryThreshold = 3;
    public int RecoveryThreshold { get => _recoveryThreshold; set => SetProperty(ref _recoveryThreshold, value); }
    private int _recoveryStabilization = 10;
    public int RecoveryStabilization { get => _recoveryStabilization; set => SetProperty(ref _recoveryStabilization, value); }
    private int _minimumSwitchHoldTime = 15;
    public int MinimumSwitchHoldTime { get => _minimumSwitchHoldTime; set => SetProperty(ref _minimumSwitchHoldTime, value); }
    private string _activeIPv4 = "By policy";
    public string ActiveIPv4 { get => _activeIPv4; set => SetProperty(ref _activeIPv4, value); }
    private string _activeIPv6 = "By policy";
    public string ActiveIPv6 { get => _activeIPv6; set => SetProperty(ref _activeIPv6, value); }
    private string _state = "Ready";
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public IReadOnlyList<Guid> OrderedInterfaceCandidates { get; set; } = [];

    public static PolicyRow FromJson(JsonElement item) => new()
    {
        Id = JsonValue.Guid(item, "Id"),
        Name = JsonValue.String(item, "Routing policy", "Name"),
        PrimaryInterfaceId = JsonValue.Guid(item, "PrimaryInterfaceId"),
        FallbackInterfaceId = Guid.TryParse(JsonValue.String(item, string.Empty, "FallbackInterfaceId"), out var fallback) ? fallback : null,
        FailoverEnabled = JsonValue.Bool(item, false, "FailoverEnabled"),
        AutoFailback = JsonValue.Bool(item, false, "AutoFailback"),
        Enabled = JsonValue.Bool(item, true, "Enabled"),
        FailureThreshold = JsonValue.Int32(item, 3, "FailureThreshold"),
        RecoveryThreshold = JsonValue.Int32(item, 3, "RecoveryThreshold"),
        RecoveryStabilization = JsonValue.Seconds(item, 10, "RecoveryStabilization"),
        MinimumSwitchHoldTime = JsonValue.Seconds(item, 15, "MinimumSwitchHoldTime"),
        ActiveIPv4 = JsonValue.String(item, "By policy", "ActiveIPv4Interface", "CurrentIPv4Interface", "ActiveIPv4"),
        ActiveIPv6 = JsonValue.String(item, "By policy", "ActiveIPv6Interface", "CurrentIPv6Interface", "ActiveIPv6"),
        State = JsonValue.String(item, "Ready", "State", "FailoverState"),
        OrderedInterfaceCandidates = JsonValue.Items(JsonValue.Property(item, "OrderedInterfaceCandidates"))
            .Select(element => Guid.TryParse(element.ToString(), out var candidate) ? candidate : Guid.Empty).Where(id => id != Guid.Empty).ToArray()
    };

    public object ToWire() => new
    {
        Id = Id.ToString("D"),
        Name,
        PrimaryInterfaceId = PrimaryInterfaceId.ToString("D"),
        FallbackInterfaceId = FallbackInterfaceId?.ToString("D"),
        FailoverEnabled,
        AutoFailback,
        Enabled,
        FailureThreshold,
        RecoveryThreshold,
        RecoveryStabilization = TimeSpan.FromSeconds(RecoveryStabilization),
        MinimumSwitchHoldTime = TimeSpan.FromSeconds(MinimumSwitchHoldTime),
        OrderedInterfaceCandidates = OrderedInterfaceCandidates.Select(id => id.ToString("D")).ToArray()
    };
}

public sealed class RuleRow : ObservableObject
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = "Application";
    public string ExecutablePath { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string ToggleText => Enabled ? "Disable" : "Enable";
    public Guid PolicyId { get; init; }
    public int Priority { get; init; }
    public string PolicyName { get; init; } = "—";
    public string IPv4Route { get; set; } = "By policy";
    public string IPv6Route { get; set; } = "By policy";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public static RuleRow FromJson(JsonElement item, IReadOnlyDictionary<Guid, string> policies)
    {
        var policyId = JsonValue.Guid(item, "PolicyId");
        var path = JsonValue.String(item, string.Empty, "ExecutablePath");
        return new RuleRow
        {
            Id = JsonValue.Guid(item, "Id"),
            DisplayName = JsonValue.String(item, Path.GetFileNameWithoutExtension(path), "DisplayName", "ProcessName"),
            ExecutablePath = path,
            ProcessName = JsonValue.String(item, string.Empty, "ProcessName"),
            Enabled = JsonValue.Bool(item, true, "Enabled"),
            PolicyId = policyId,
            Priority = JsonValue.Int32(item, 0, "Priority"),
            PolicyName = policies.TryGetValue(policyId, out var name) ? name : "—",
            IPv4Route = JsonValue.String(item, "By policy", "IPv4Route", "ActiveIPv4Interface"),
            IPv6Route = JsonValue.String(item, "By policy", "IPv6Route", "ActiveIPv6Interface"),
            CreatedAt = JsonValue.DateTimeOffset(item, "CreatedAt"),
            UpdatedAt = JsonValue.DateTimeOffset(item, "UpdatedAt")
        };
    }
}

public sealed class RuleDraft : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _displayName = string.Empty;
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    private string _executablePath = string.Empty;
    public string ExecutablePath { get => _executablePath; set => SetProperty(ref _executablePath, value); }
    private string _processName = string.Empty;
    public string ProcessName { get => _processName; set => SetProperty(ref _processName, value); }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    private Guid _policyId;
    public Guid PolicyId { get => _policyId; set => SetProperty(ref _policyId, value); }
    private int _priority = 100;
    public int Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void CopyFrom(RuleRow rule)
    {
        Id = rule.Id;
        DisplayName = rule.DisplayName;
        ExecutablePath = rule.ExecutablePath;
        ProcessName = rule.ProcessName;
        Enabled = rule.Enabled;
        PolicyId = rule.PolicyId;
        Priority = rule.Priority;
        CreatedAt = rule.CreatedAt ?? DateTimeOffset.UtcNow;
    }
}

public sealed record LogRow(string Timestamp, string Level, string Source, string Message)
{
    public static LogRow FromJson(JsonElement item) => new(
        JsonValue.String(item, "—", "Timestamp", "Time", "CreatedAt"),
        JsonValue.String(item, "Information", "Level"),
        JsonValue.String(item, "Service", "Source", "Category"),
        JsonValue.String(item, item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText(), "Message", "Text"));
}

public sealed record RunningProcessOption(string ProcessName, string ExecutablePath, string Pids)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ExecutablePath) ? $"{ProcessName} · {Pids}" : $"{ProcessName} · {Path.GetFileName(ExecutablePath)} · {Pids}";

    public static RunningProcessOption FromJson(JsonElement item) => new(
        JsonValue.String(item, "Unknown process", "ProcessName"),
        JsonValue.String(item, string.Empty, "ExecutablePath"),
        JsonValue.StringList(item, "Pids"));
}

public sealed class EndpointRow : ObservableObject
{
    public Guid Id { get; init; }
    private string _name = "Probe endpoint";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    private string _url = string.Empty;
    public string Url { get => _url; set => SetProperty(ref _url, value); }
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    private bool _requireIPv4 = true;
    public bool RequireIPv4 { get => _requireIPv4; set => SetProperty(ref _requireIPv4, value); }
    private bool _requireIPv6 = true;
    public bool RequireIPv6 { get => _requireIPv6; set => SetProperty(ref _requireIPv6, value); }

    public static EndpointRow FromJson(JsonElement item) => new()
    {
        Id = JsonValue.Guid(item, "Id"),
        Name = JsonValue.String(item, "Probe endpoint", "Name"),
        Url = JsonValue.String(item, string.Empty, "Url"),
        Enabled = JsonValue.Bool(item, true, "Enabled"),
        RequireIPv4 = JsonValue.Bool(item, true, "RequireIPv4"),
        RequireIPv6 = JsonValue.Bool(item, true, "RequireIPv6")
    };

    public object ToWire() => new { Id, Name, Url, Enabled, RequireIPv4, RequireIPv6 };
}

public sealed class DashboardStatus : ObservableObject
{
    private string _coreState = "Unknown";
    public string CoreState { get => _coreState; set => SetProperty(ref _coreState, value); }
    private string _version = "—";
    public string Version { get => _version; set => SetProperty(ref _version, value); }
    private string _uptime = "—";
    public string Uptime { get => _uptime; set => SetProperty(ref _uptime, value); }
    private string _lastError = string.Empty;
    public string LastError { get => _lastError; set => SetProperty(ref _lastError, value); }
    private string _lastExitCode = "—";
    public string LastExitCode { get => _lastExitCode; set => SetProperty(ref _lastExitCode, value); }
    private bool _routingEnabled;
    public bool RoutingEnabled { get => _routingEnabled; set => SetProperty(ref _routingEnabled, value); }
    public static DashboardStatus FromJson(JsonElement item) => new()
    {
        CoreState = JsonValue.Bool(item, false, "CoreFaulted")
            ? "Faulted"
            : JsonValue.String(item,
                JsonValue.Bool(item, false, "CoreRunning", "IsCoreRunning") ? "Running" : "Stopped",
                "CoreState", "CoreStatus", "SingBoxState"),
        Version = JsonValue.String(item, "—", "SingBoxVersion", "CoreVersion", "Version"),
        Uptime = JsonValue.String(item, "—", "Uptime", "CoreUptime"),
        LastError = JsonValue.String(item, string.Empty, "LastError", "Error"),
        LastExitCode = JsonValue.DisplayValue(item, "LastExitCode"),
        RoutingEnabled = JsonValue.Bool(item, false, "RoutingEnabled", "Enabled")
    };
}

public sealed record StatusCard(string Label, string Value, string Detail);
public sealed record DiagnosticLine(string Name, string Value);
