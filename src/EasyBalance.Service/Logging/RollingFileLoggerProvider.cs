using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyBalance.Service.Logging;

public sealed class RollingFileLoggerOptions
{
    public string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EasyBalance", "logs", "service.log");
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
    public int RetainedFileCount { get; set; } = 5;
    public long MaxTotalSizeBytes { get; set; } = 40 * 1024 * 1024;
    public int QueueCapacity { get; set; } = 4096;
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// A bounded, asynchronous file logger. The active file is rotated to numbered siblings,
/// and old archives are pruned to satisfy both count and total-byte limits.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const int MaximumBatchSize = 128;

    private readonly string _filePath;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly long _maxFileSizeBytes;
    private readonly int _retainedFileCount;
    private readonly long _maxTotalSizeBytes;
    private readonly LogLevel _minimumLogLevel;
    private readonly Channel<LogRecord> _queue;
    private readonly ConcurrentDictionary<string, Logger> _loggers = new(StringComparer.Ordinal);
    private readonly Task _writerTask;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private FileStream? _activeStream;
    private long _activeLength;
    private long _droppedRecords;
    private int _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions? options = null)
    {
        options ??= new RollingFileLoggerOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFileSizeBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainedFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTotalSizeBytes, options.MaxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueCapacity, 1);

        _filePath = Path.GetFullPath(options.FilePath);
        _directory = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("The log file path must have a parent directory.", nameof(options));
        _fileName = Path.GetFileName(_filePath);
        _maxFileSizeBytes = options.MaxFileSizeBytes;
        _retainedFileCount = options.RetainedFileCount;
        _maxTotalSizeBytes = options.MaxTotalSizeBytes;
        _minimumLogLevel = options.MinimumLogLevel;

        Directory.CreateDirectory(_directory);
        if (File.Exists(_filePath) && new FileInfo(_filePath).Length >= _maxFileSizeBytes)
        {
            RotateFiles();
        }
        PruneArchives();

        _queue = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _activeLength = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>Number of log records skipped because the bounded queue was full.</summary>
    public long DroppedRecordCount => Interlocked.Read(ref _droppedRecords);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new Logger(this, name));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        Volatile.Write(ref _scopeProvider, scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider)));

    public void Dispose()
    {
        CompleteAndWait();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        await _writerTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void CompleteAndWait()
    {
        Complete();
        _writerTask.GetAwaiter().GetResult();
    }

    private void Complete()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _queue.Writer.TryComplete();
        }
    }

    private bool IsEnabled(LogLevel level) =>
        level != LogLevel.None && level >= _minimumLogLevel && Volatile.Read(ref _disposed) == 0;

    private void Enqueue(LogRecord record)
    {
        if (!_queue.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _droppedRecords);
        }
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<LogRecord>(MaximumBatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaximumBatchSize && _queue.Reader.TryRead(out var record))
                {
                    batch.Add(record);
                }

                foreach (var record in batch)
                {
                    await WriteRecordAsync(record.Text).ConfigureAwait(false);
                }

                if (_activeStream is not null)
                {
                    await _activeStream.FlushAsync().ConfigureAwait(false);
                }

                PruneArchives();
            }
        }
        catch (Exception exception)
        {
            // Keep a logging I/O failure from taking down the Windows Service host.
            try
            {
                Console.Error.WriteLine($"EasyBalance rolling logger stopped: {exception}");
            }
            catch (IOException)
            {
                // Console output may not be available when running as a Windows Service.
            }
        }
        finally
        {
            if (_activeStream is not null)
            {
                await _activeStream.DisposeAsync().ConfigureAwait(false);
                _activeStream = null;
            }
        }
    }

    private async ValueTask WriteRecordAsync(string text)
    {
        var bytes = EncodeLine(text, checked((int)Math.Min(_maxFileSizeBytes, int.MaxValue)));
        if (_activeLength > 0 && _activeLength + bytes.Length > _maxFileSizeBytes)
        {
            await CloseActiveStreamAsync().ConfigureAwait(false);
            RotateFiles();
            _activeLength = 0;
        }

        _activeStream ??= new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous);

        await _activeStream.WriteAsync(bytes).ConfigureAwait(false);
        _activeLength += bytes.Length;
    }

    private async ValueTask CloseActiveStreamAsync()
    {
        if (_activeStream is null)
        {
            return;
        }

        await _activeStream.FlushAsync().ConfigureAwait(false);
        await _activeStream.DisposeAsync().ConfigureAwait(false);
        _activeStream = null;
    }

    private static byte[] EncodeLine(string text, int maxBytes)
    {
        var bytes = Utf8.GetBytes(text.EndsWith('\n') ? text : text + '\n');
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        // Truncate only at UTF-8 code point boundaries and preserve the line terminator.
        var contentLength = maxBytes - 1;
        while (contentLength > 0 && (bytes[contentLength] & 0xC0) == 0x80)
        {
            contentLength--;
        }

        var truncated = new byte[contentLength + 1];
        Buffer.BlockCopy(bytes, 0, truncated, 0, contentLength);
        truncated[^1] = (byte)'\n';
        return truncated;
    }

    private void RotateFiles()
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length == 0)
        {
            PruneArchives();
            return;
        }

        if (_retainedFileCount == 1)
        {
            File.Delete(_filePath);
        }
        else
        {
            for (var index = _retainedFileCount - 1; index >= 1; index--)
            {
                var destination = GetArchivePath(index);
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                var source = index == 1 ? _filePath : GetArchivePath(index - 1);
                if (File.Exists(source))
                {
                    File.Move(source, destination);
                }
            }
        }

        PruneArchives();
    }

    private void PruneArchives()
    {
        var archives = EnumerateArchives()
            .OrderByDescending(item => item.Index)
            .ToList();
        var totalBytes = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
        totalBytes += archives.Sum(item => new FileInfo(item.Path).Length);

        while (archives.Count > _retainedFileCount - 1 || totalBytes > _maxTotalSizeBytes)
        {
            if (archives.Count == 0)
            {
                break;
            }

            var oldest = archives[0];
            archives.RemoveAt(0);
            var length = new FileInfo(oldest.Path).Length;
            File.Delete(oldest.Path);
            totalBytes -= length;
        }
    }

    private IEnumerable<(int Index, string Path)> EnumerateArchives()
    {
        var prefix = _fileName + ".";
        foreach (var path in Directory.EnumerateFiles(_directory, _fileName + ".*"))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                index > 0)
            {
                yield return (index, path);
            }
        }
    }

    private string GetArchivePath(int index) => $"{_filePath}.{index}";

    private IExternalScopeProvider ScopeProvider => Volatile.Read(ref _scopeProvider);

    private sealed record LogRecord(string Text);

    private sealed class Logger(RollingFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider.ScopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var builder = new StringBuilder(256)
                .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(categoryName);

            if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
            {
                builder.Append(" [").Append(eventId.Id);
                if (!string.IsNullOrEmpty(eventId.Name))
                {
                    builder.Append(':').Append(eventId.Name);
                }
                builder.Append(']');
            }

            builder.Append(" - ").Append(message);
            var scopes = new List<string>();
            provider.ScopeProvider.ForEachScope(static (scope, target) =>
                target.Add(scope?.ToString() ?? string.Empty), scopes);
            if (scopes.Count > 0)
            {
                builder.Append(" | scopes: ").AppendJoin(" => ", scopes);
            }

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            provider.Enqueue(new LogRecord(builder.ToString()));
        }
    }
}
