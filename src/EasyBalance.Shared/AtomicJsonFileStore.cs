using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EasyBalance.Shared;

/// <summary>
/// Reads and writes JSON with caller-supplied source-generated metadata. Writes are staged
/// beside the destination, flushed, then atomically moved over the destination.
/// </summary>
public sealed class AtomicJsonFileStore
{
    public async ValueTask<T?> ReadAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination must include a parent directory.", nameof(path));

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Preserve the original write/move exception. An orphaned staging file is safe.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original write/move exception. An orphaned staging file is safe.
            }
        }
    }
}
