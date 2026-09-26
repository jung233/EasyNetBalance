using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasyBalance.Service.Core;

/// <summary>
/// Encapsulates the loopback-only Clash-compatible REST endpoints implemented by sing-box.
/// The rest of the service depends on ISingBoxControlClient and does not handle API details.
/// </summary>
public sealed class SingBoxControlClient : ISingBoxControlClient, IDisposable
{
    private static readonly TimeSpan[] SwitchRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250)
    ];

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _stateLock = new();
    private Uri? _endpoint;
    private string? _secret;

    public SingBoxControlClient() : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    public SingBoxControlClient(HttpClient httpClient) : this(httpClient, ownsHttpClient: false)
    {
    }

    private SingBoxControlClient(HttpClient httpClient, bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Configure(Uri endpoint, string secret)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The sing-box control endpoint must use HTTP on a loopback IP address.", nameof(endpoint));
        }

        lock (_stateLock)
        {
            _endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
            _secret = secret;
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            _endpoint = null;
            _secret = null;
        }
    }

    public async Task<IReadOnlyList<SingBoxOutboundInfo>> GetOutboundsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "proxies", content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("proxies", out var proxies) || proxies.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The sing-box control API returned an invalid outbound list.");
        }

        var result = new List<SingBoxOutboundInfo>();
        foreach (var proxy in proxies.EnumerateObject())
        {
            if (string.Equals(proxy.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase) || proxy.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(proxy.Value, "type") ?? string.Empty;
            var selected = GetString(proxy.Value, "now");
            var members = proxy.Value.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : Array.Empty<string>();
            result.Add(new SingBoxOutboundInfo(proxy.Name, type, selected, members));
        }

        return result;
    }

    public async Task<SingBoxConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "connections", content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The sing-box control API returned an invalid connections snapshot.");
        }

        var connections = new List<SingBoxConnectionInfo>();
        if (TryGetPropertyIgnoreCase(root, "connections", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var metadata = TryGetPropertyIgnoreCase(item, "metadata", out var metadataValue) && metadataValue.ValueKind == JsonValueKind.Object
                    ? metadataValue
                    : default;
                connections.Add(new SingBoxConnectionInfo(
                    ReadOptionalString(item, "id") ?? string.Empty,
                    ReadOptionalString(metadata, "network") ?? string.Empty,
                    ReadOptionalString(metadata, "sourceIP"),
                    ReadOptionalString(metadata, "sourcePort"),
                    ReadOptionalString(metadata, "destinationIP"),
                    ReadOptionalString(metadata, "destinationPort"),
                    ReadOptionalString(metadata, "host"),
                    ReadOptionalString(metadata, "process"),
                    ReadOptionalString(metadata, "processPath"),
                    ReadStringArray(item, "chains"),
                    ReadCounter(item, "upload"),
                    ReadCounter(item, "download"),
                    ReadTimestamp(item, "start")));
            }
        }

        return new SingBoxConnectionsSnapshot(
            ReadCounter(root, "uploadTotal"),
            ReadCounter(root, "downloadTotal"),
            connections);
    }

    public async Task<SingBoxSelectorState?> GetSelectorStateAsync(string selectorTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorTag);
        using var response = await SendAsync(HttpMethod.Get, $"proxies/{Uri.EscapeDataString(selectorTag)}", content: null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!string.Equals(GetString(root, "type"), "Selector", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var selected = GetString(root, "now");
        var available = root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array
            ? all.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(selected))
        {
            throw new InvalidDataException($"Selector '{selectorTag}' did not report its active outbound.");
        }

        return new SingBoxSelectorState(selectorTag, selected, available);
    }

    public async Task<SingBoxSelectorState> SwitchSelectorAsync(
        string selectorTag,
        string outboundTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundTag);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= SwitchRetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var content = new StringContent(
                    new JsonObject { ["name"] = outboundTag }.ToJsonString(JsonNodeSerialization.Compact),
                    Encoding.UTF8,
                    "application/json");
                using var response = await SendAsync(
                    HttpMethod.Put,
                    $"proxies/{Uri.EscapeDataString(selectorTag)}",
                    content,
                    cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

                var state = await GetSelectorStateAsync(selectorTag, cancellationToken).ConfigureAwait(false);
                if (state is not null && string.Equals(state.SelectedOutboundTag, outboundTag, StringComparison.OrdinalIgnoreCase))
                {
                    return state;
                }

                lastError = new InvalidOperationException(
                    $"sing-box did not confirm '{outboundTag}' as the active outbound for selector '{selectorTag}'.");
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or InvalidOperationException)
            {
                lastError = exception;
            }

            if (attempt < SwitchRetryDelays.Length)
            {
                await Task.Delay(SwitchRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Could not switch selector '{selectorTag}' to '{outboundTag}' after three bounded attempts.",
            lastError);
    }

    public async Task<TimeSpan?> TestOutboundAsync(
        string outboundTag,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundTag);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Outbound tests require an absolute HTTPS endpoint.", nameof(endpoint));
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The API supports a timeout between zero and 30 seconds.");
        }

        var timeoutMilliseconds = (int)Math.Ceiling(timeout.TotalMilliseconds);
        var apiEndpoint = new UriBuilder(GetEndpoint())
        {
            Path = $"/proxies/{Uri.EscapeDataString(outboundTag)}/delay",
            Query = $"url={Uri.EscapeDataString(endpoint.AbsoluteUri)}&timeout={timeoutMilliseconds}"
        }.Uri;

        using var request = CreateRequest(HttpMethod.Get, apiEndpoint, content: null);
        using var testCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        testCancellation.CancelAfter(timeout + TimeSpan.FromSeconds(1));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, testCancellation.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, testCancellation.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(testCancellation.Token).ConfigureAwait(false), cancellationToken: testCancellation.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("delay", out var delay) || !delay.TryGetInt32(out var milliseconds) || milliseconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public void Dispose()
    {
        Clear();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var endpoint = GetEndpoint();
        using var request = CreateRequest(method, new Uri(endpoint, relativePath), content);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, HttpContent? content)
    {
        var (_, secret) = GetSession();
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return request;
    }

    private Uri GetEndpoint()
    {
        var (endpoint, _) = GetSession();
        return endpoint;
    }

    private (Uri Endpoint, string Secret) GetSession()
    {
        lock (_stateLock)
        {
            if (_endpoint is null || _secret is null)
            {
                throw new InvalidOperationException("The sing-box control client has not been configured for a running core.");
            }

            return (_endpoint, _secret);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 300)
        {
            body = body[..300];
        }

        throw new HttpRequestException(
            $"sing-box control API returned {(int)response.StatusCode} ({response.StatusCode}): {body}",
            inner: null,
            response.StatusCode);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrEmpty(item))
            .Select(item => item!)
            .ToArray();
    }

    private static long ReadCounter(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var signed))
            {
                return Math.Max(0, signed);
            }

            if (value.TryGetUInt64(out var unsigned))
            {
                return unsigned > long.MaxValue ? long.MaxValue : (long)unsigned;
            }
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(0, parsed);
        }

        return 0;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    }, disposeHandler: true);
}
