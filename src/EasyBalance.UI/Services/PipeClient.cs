using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using EasyBalance.Shared;

namespace EasyBalance.UI.Services;

public sealed class PipeClient
{
    public const string PipeName = IpcConstants.PipeName;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds(method)));
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            var request = new PipeRequest(method, payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonOptions));
            var requestLine = JsonSerializer.Serialize(request, IpcJsonContext.Default.PipeRequest) + "\n";
            var bytes = Encoding.UTF8.GetBytes(requestLine);
            await pipe.WriteAsync(bytes, timeout.Token);
            await pipe.FlushAsync(timeout.Token);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new InvalidDataException("The EasyBalance service returned an empty response.");
            }

            var response = JsonSerializer.Deserialize(responseLine, IpcJsonContext.Default.PipeResponse)
                ?? throw new InvalidDataException("The EasyBalance service returned an unreadable response.");
            if (!response.Success)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error)
                    ? "The service could not complete the request."
                    : response.Error);
            }

            return response.Payload ?? JsonSerializer.SerializeToElement<object?>(null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The EasyBalance service did not respond within {TimeoutSeconds(method)} seconds.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Cannot connect to the EasyBalance service. Check that the service is installed and running.", ex);
        }
    }

    public async Task<T?> GetAsync<T>(string method, CancellationToken cancellationToken = default)
    {
        var payload = await CallAsync(method, cancellationToken: cancellationToken);
        return payload.ValueKind == JsonValueKind.Null ? default : payload.Deserialize<T>(JsonOptions);
    }

    private static int TimeoutSeconds(string method) => method is
        "SaveRule" or "DeleteRule" or "SavePolicy" or "DeletePolicy" or "SetDefaultPolicy" or "SaveSettings" or
        "SetInterfaceUsability" or "EnableRouting" or "DisableRouting" or "RestartCore" or "ValidateConfig" or "ExportDiagnostics" or "TestInterface"
        ? 45
        : 5;
}
