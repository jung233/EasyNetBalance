using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyBalance.Shared;

namespace EasyBalance.UI.Services;

public sealed class PipeClient
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(method is "GetConnectionTelemetry" ? 8 : 45));
        await using var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            var request = new PipeRequest(method, payload is null ? null : JsonSerializer.SerializeToElement(payload, Options));
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, IpcJsonContext.Default.PipeRequest) + "\n"), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("The service returned an empty response.");
            var result = JsonSerializer.Deserialize(line, IpcJsonContext.Default.PipeResponse)
                ?? throw new InvalidDataException("The service returned an unreadable response.");
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "The service could not complete the request.");
            return result.Payload ?? JsonSerializer.SerializeToElement<object?>(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The service did not respond in time. Check that EasyBalance is running.");
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("The EasyBalance service is unavailable.", exception);
        }
    }

    public async Task<T> GetAsync<T>(string method, CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(method, cancellationToken: cancellationToken);
        return value.Deserialize<T>(Options) ?? throw new InvalidDataException($"Missing {method} data.");
    }

    public async Task<T> GetWithPayloadAsync<T>(string method, object payload, CancellationToken cancellationToken = default)
    {
        var value = await CallAsync(method, payload, cancellationToken);
        return value.Deserialize<T>(Options) ?? throw new InvalidDataException($"Missing {method} data.");
    }
}
