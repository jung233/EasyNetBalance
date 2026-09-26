namespace EasyBalance.Service.Core;

/// <summary>
/// Business-facing access to sing-box outbounds and policy selectors.
/// </summary>
public interface ISingBoxControlClient
{
    /// <summary>Configures the local, loopback-only control endpoint for the active core process.</summary>
    void Configure(Uri endpoint, string secret);

    /// <summary>Removes the active endpoint and in-memory secret.</summary>
    void Clear();

    Task<IReadOnlyList<SingBoxOutboundInfo>> GetOutboundsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a read-only snapshot of active Clash API connections and cumulative traffic totals.</summary>
    Task<SingBoxConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken = default);

    Task<SingBoxSelectorState?> GetSelectorStateAsync(string selectorTag, CancellationToken cancellationToken = default);

    /// <summary>Switches a selector and confirms the selected outbound with a fresh API query.</summary>
    Task<SingBoxSelectorState> SwitchSelectorAsync(
        string selectorTag,
        string outboundTag,
        CancellationToken cancellationToken = default);

    /// <summary>Tests the specified direct outbound against an HTTPS endpoint.</summary>
    Task<TimeSpan?> TestOutboundAsync(
        string outboundTag,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record SingBoxOutboundInfo(
    string Tag,
    string Type,
    string? SelectedOutboundTag,
    IReadOnlyList<string> AvailableOutboundTags)
{
    public bool IsSelector => string.Equals(Type, "Selector", StringComparison.OrdinalIgnoreCase);
}

public sealed record SingBoxSelectorState(
    string Tag,
    string SelectedOutboundTag,
    IReadOnlyList<string> AvailableOutboundTags);

/// <summary>A point-in-time view of active connections and the core's cumulative traffic counters.</summary>
public sealed record SingBoxConnectionsSnapshot(
    long UploadTotal,
    long DownloadTotal,
    IReadOnlyList<SingBoxConnectionInfo> Connections);

/// <summary>Connection metadata exposed by sing-box's Clash-compatible /connections endpoint.</summary>
public sealed record SingBoxConnectionInfo(
    string Id,
    string Network,
    string? SourceIp,
    string? SourcePort,
    string? DestinationIp,
    string? DestinationPort,
    string? DestinationHost,
    string? Process,
    string? ProcessPath,
    IReadOnlyList<string> Chains,
    long Upload,
    long Download,
    DateTimeOffset? Start);
