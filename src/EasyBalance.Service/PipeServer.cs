using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using EasyBalance.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EasyBalance.Service;

public sealed class PipeServer(EasyBalanceRuntime runtime, ILogger<PipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException exception) { logger.LogWarning(exception, "Named pipe client disconnected"); }
            catch (Exception exception) { logger.LogError(exception, "Named pipe request failed"); }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(IpcConstants.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        { AutoFlush = true };
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        string? line;
        bool tooLong;
        try
        {
            (line, tooLong) = await ReadRequestLineAsync(reader, 1024 * 1024, readTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Closing an idle named pipe client after the request-read timeout.");
            return;
        }

        if (tooLong)
        {
            await WriteResponseAsync(writer, new PipeResponse { Error = "Request exceeds 1 MiB." }, cancellationToken);
            return;
        }
        if (line is null) return;
        PipeResponse response;
        try
        {
            var request = JsonSerializer.Deserialize(line, IpcJsonContext.Default.PipeRequest)
                ?? throw new JsonException("Empty request");
            response = await runtime.HandleAsync(request, cancellationToken);
        }
        catch (JsonException exception)
        {
            response = new PipeResponse { Error = $"Invalid request: {exception.Message}" };
        }
        await WriteResponseAsync(writer, response, cancellationToken);
    }

    private static async Task<(string? Line, bool TooLong)> ReadRequestLineAsync(
        StreamReader reader,
        int maximumUtf8Bytes,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder(Math.Min(maximumUtf8Bytes, 4096));
        var utf8Bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return (line.Length == 0 ? null : line.ToString(), false);
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    return (line.ToString(), false);
                }

                var characterBytes = character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                if (utf8Bytes > maximumUtf8Bytes - characterBytes)
                {
                    return (null, true);
                }

                utf8Bytes += characterBytes;
                line.Append(character);
            }
        }
    }

    private static Task WriteResponseAsync(StreamWriter writer, PipeResponse response, CancellationToken cancellationToken) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJsonContext.Default.PipeResponse)
            .AsMemory(), cancellationToken);
}
