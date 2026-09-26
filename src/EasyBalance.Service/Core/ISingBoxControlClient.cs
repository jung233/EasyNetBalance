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
