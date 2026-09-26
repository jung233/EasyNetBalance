namespace EasyBalance.UI.Models;

public sealed class RuntimeStatus
{
    public bool RoutingEnabled { get; set; }
    public bool CoreRunning { get; set; }
    public bool CoreFaulted { get; set; }
    public string? Version { get; set; }
    public TimeSpan? Uptime { get; set; }
    public string? LastError { get; set; }
    public List<PolicyRouteStatus> Policies { get; set; } = [];
}
public sealed class PolicyRouteStatus
{
    public string PolicyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ActiveIPv4Interface { get; set; }
    public string? ActiveIPv6Interface { get; set; }
    public bool NoHealthyIPv4Interface { get; set; }
    public bool NoHealthyIPv6Interface { get; set; }
}
public sealed class RuntimeLogEntry
{
    public DateTimeOffset Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string TimeText => Timestamp.ToLocalTime().ToString("G");
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
    public long ServiceWorkingSetBytes { get; set; }
    public long? SingBoxWorkingSetBytes { get; set; }
    public int GeneratedRouteRules { get; set; }
}
public sealed class ConnectionTelemetry
{
    public DateTimeOffset SampledAt { get; set; }
    public string? LastError { get; set; }
    public long UploadTotal { get; set; }
    public long DownloadTotal { get; set; }
    public List<OutboundTraffic> Outbounds { get; set; } = [];
    public List<LiveConnection> Connections { get; set; } = [];
}
public sealed class OutboundTraffic
{
    public string InterfaceId { get; set; } = "";
    public string Name { get; set; } = "";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public int ActiveConnections { get; set; }
    public int? TargetPercent { get; set; }
    public bool Available { get; set; }
    public string? Error { get; set; }
}
public sealed class LiveConnection
{
    public string Process { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public string DestinationIp { get; set; } = "";
    public string DestinationHost { get; set; } = "";
    public string DestinationPort { get; set; } = "";
    public string Network { get; set; } = "";
    public string ActualOutbound { get; set; } = "";
    public string PredictedOutbound { get; set; } = "";
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public string Destination => string.IsNullOrWhiteSpace(DestinationHost) ? $"{DestinationIp}:{DestinationPort}" : $"{DestinationHost} ({DestinationIp}:{DestinationPort})";
    public string UploadText => Display.Bytes(UploadBytes);
    public string DownloadText => Display.Bytes(DownloadBytes);
}
public sealed class OutboundRow
{
    public string Name { get; set; } = "";
    public string UploadRate { get; set; } = "Sampling…";
    public string DownloadRate { get; set; } = "Sampling…";
    public string UploadTotal { get; set; } = "";
    public string DownloadTotal { get; set; } = "";
    public string Connections { get; set; } = "";
    public string Target { get; set; } = "—";
    public string State { get; set; } = "";
}
public static class Display
{
    public static string Bytes(long bytes)
    {
        var amount = (double)Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        while (amount >= 1000 && index < units.Length - 1) { amount /= 1000; index++; }
        return $"{amount:0.#} {units[index]}";
    }
}
