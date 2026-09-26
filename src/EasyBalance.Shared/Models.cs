using System.Net.NetworkInformation;
using System.Text.Json.Serialization;

namespace EasyBalance.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<HealthState>))]
public enum HealthState
{
    Unknown,
    Healthy,
    Suspect,
    Down,
    Recovering,
    Unavailable
}

[JsonConverter(typeof(JsonStringEnumConverter<AddressFamilyKind>))]
public enum AddressFamilyKind
{
    IPv4,
    IPv6
}

public sealed class RoutingPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Default";
    public string PrimaryInterfaceId { get; set; } = string.Empty;
    public string? FallbackInterfaceId { get; set; }
    public bool FailoverEnabled { get; set; } = true;
    public bool AutoFailback { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int FailureThreshold { get; set; } = 3;
    public int RecoveryThreshold { get; set; } = 3;
    public TimeSpan RecoveryStabilization { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MinimumSwitchHoldTime { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Optional ordered candidates reserved for policy chains. The MVP uses the primary
    /// and fallback fields when this list is empty.
    /// </summary>
    public List<string> OrderedInterfaceCandidates { get; set; } = [];
}

public sealed class ApplicationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string? ProcessName { get; set; }
    public bool Enabled { get; set; } = true;
    public string PolicyId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProbeEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool RequireIPv4 { get; set; } = true;
    public bool RequireIPv6 { get; set; } = true;
}

public sealed class AppSettings
{
    public string? SingBoxPath { get; set; }
    public bool Enabled { get; set; }
    public bool StrictRoute { get; set; }
    public bool Ipv6Enabled { get; set; } = true;
    public bool DisconnectOldConnectionsOnFailover { get; set; }
    public bool ShowVirtualInterfaces { get; set; }
    public TimeSpan HealthyProbeInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan SuspectProbeInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan DownProbeInterval { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public RoutingPolicy DefaultPolicy { get; set; } = new();
    public List<RoutingPolicy> Policies { get; set; } = [];
    public List<ApplicationRule> ApplicationRules { get; set; } = [];
    public List<ProbeEndpoint> ProbeEndpoints { get; set; } = CreateDefaultProbeEndpoints();

    /// <summary>
    /// Per-adapter usability choices keyed by canonical Windows interface GUID.
    /// A value of true or false overrides automatic classification.
    /// </summary>
    public Dictionary<string, bool> InterfaceUsabilityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings CreateDefault() => new();

    private static List<ProbeEndpoint> CreateDefaultProbeEndpoints() =>
    [
        new() { Name = "Microsoft connectivity", Url = "https://www.msftconnecttest.com/connecttest.txt" },
        new() { Name = "Cloudflare connectivity", Url = "https://www.cloudflare.com/cdn-cgi/trace" },
        new() { Name = "Google connectivity", Url = "https://www.gstatic.com/generate_204" }
    ];
}

public sealed class NetworkAdapterInfo
{
    /// <summary>Canonical interface GUID (D format) derived from NetworkInterface.Id.</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter<NetworkInterfaceType>))]
    public NetworkInterfaceType NetworkInterfaceType { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<OperationalStatus>))]
    public OperationalStatus OperationalStatus { get; set; }
    public long Speed { get; set; }
    public List<string> IPv4Addresses { get; set; } = [];
    public List<string> IPv6Addresses { get; set; } = [];
    public List<string> IPv4Gateways { get; set; } = [];
    public List<string> IPv6Gateways { get; set; } = [];
    public List<string> DnsServers { get; set; } = [];
    public bool IsPhysical { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsUserAllowed { get; set; }
    public HealthState IPv4Health { get; set; } = HealthState.Unknown;
    public HealthState IPv6Health { get; set; } = HealthState.Unknown;
    public TimeSpan? IPv4Latency { get; set; }
    public TimeSpan? IPv6Latency { get; set; }
    public DateTimeOffset? LastProbeTime { get; set; }
    public DateTimeOffset? LastStateChange { get; set; }
}

public sealed class PipeRequest
{
    public PipeRequest() { }

    public PipeRequest(string method, System.Text.Json.JsonElement? payload)
    {
        Method = method;
        Payload = payload;
    }

    public string Method { get; set; } = string.Empty;
    public System.Text.Json.JsonElement? Payload { get; set; }
}

public sealed class PipeResponse
{
    public PipeResponse() { }

    public PipeResponse(bool success, string? error, System.Text.Json.JsonElement? payload)
    {
        Success = success;
        Error = error;
        Payload = payload;
    }

    public bool Success { get; set; }
    public string? Error { get; set; }
    public System.Text.Json.JsonElement? Payload { get; set; }
}

public static class IpcConstants
{
    public const string PipeName = "EasyBalance.Control.v1";
}
