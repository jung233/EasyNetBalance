using System.Text.Json.Serialization;

namespace EasyBalance.Service;

public sealed class RuntimeStatus
{
    public bool RoutingEnabled { get; set; }
    public bool CoreRunning { get; set; }
    public bool CoreFaulted { get; set; }
    public string? Version { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string? LastError { get; set; }
    public int? LastExitCode { get; set; }
    public List<PolicyRouteStatus> Policies { get; set; } = [];
}

public sealed class PolicyRouteStatus
{
    public string PolicyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ActiveIPv4Interface { get; set; }
    public string? ActiveIPv6Interface { get; set; }
    public bool NoHealthyIPv4Interface { get; set; }
    public bool NoHealthyIPv6Interface { get; set; }
}

public sealed class RuntimeDiagnostics
{
    public long HealthProbes { get; set; }
    public long SuccessfulProbes { get; set; }
    public long FailedProbes { get; set; }
    public long FailoverCount { get; set; }
    public long FailbackCount { get; set; }
    public int RulesCount { get; set; }
    public int PoliciesCount { get; set; }
    public int HealthContexts { get; set; }
    public long ServicePrivateBytes { get; set; }
    public long ServiceWorkingSetBytes { get; set; }
    public double ServiceCpuSeconds { get; set; }
    public bool CoreRunning { get; set; }
    public bool RoutingEnabled { get; set; }
    public long? SingBoxPrivateBytes { get; set; }
    public long? SingBoxWorkingSetBytes { get; set; }
    public double? SingBoxCpuSeconds { get; set; }
    public long? UiPrivateBytes { get; set; }
    public long? UiWorkingSetBytes { get; set; }
    public double? UiCpuSeconds { get; set; }
    public int GeneratedRouteRules { get; set; }
    public int GeneratedSelectors { get; set; }
    public int GeneratedDirectOutbounds { get; set; }
}

public sealed class RuntimeLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "Information";
    public string Source { get; set; } = "Service";
    public string Message { get; set; } = string.Empty;
}

public sealed class ProcessSnapshot
{
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public List<int> Pids { get; set; } = [];
}

public sealed class FilePathResult { public string Path { get; set; } = string.Empty; }
public sealed class FileContentResult { public string Json { get; set; } = string.Empty; }
public sealed class DiagnosticsBlobResult
{
    public string FileName { get; set; } = string.Empty;
    public string DataBase64 { get; set; } = string.Empty;
}
public sealed class InterfaceTestResult
{
    public string InterfaceId { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
    public double? LatencyMs { get; set; }
}

public sealed class DiagnosticMetadata
{
    public string EasyBalanceVersion { get; set; } = "0.1.0";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public string DotNetVersion { get; set; } = Environment.Version.ToString();
    public string? SingBoxVersion { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RuntimeStatus))]
[JsonSerializable(typeof(RuntimeDiagnostics))]
[JsonSerializable(typeof(List<RuntimeLogEntry>))]
[JsonSerializable(typeof(List<ProcessSnapshot>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(FilePathResult))]
[JsonSerializable(typeof(FileContentResult))]
[JsonSerializable(typeof(DiagnosticsBlobResult))]
[JsonSerializable(typeof(InterfaceTestResult))]
[JsonSerializable(typeof(DiagnosticMetadata))]
internal partial class ServiceJsonContext : JsonSerializerContext { }
