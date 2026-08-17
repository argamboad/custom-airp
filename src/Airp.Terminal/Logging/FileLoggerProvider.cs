using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Airp.Terminal.Logging;

/// <summary>
/// A minimal rolling file logger.
/// </summary>
/// <remarks>
/// <para>
/// The console belongs to the terminal UI. Anything written to it outside the shell's own
/// redraw corrupts the display, so log output goes to a file and never to standard output —
/// which is also why this exists rather than the built-in console provider.
/// </para>
/// <para>
/// Writing is queued onto a background task so a slow disk cannot stall a key press. On
/// shutdown the queue is drained before the provider is disposed.
/// </para>
/// </remarks>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly Task _writer;
    private readonly string _path;
    private bool _disposed;

    /// <summary>Initialises the provider and starts the background writer.</summary>
    /// <param name="directory">Directory to write log files into.</param>
    /// <param name="retainFiles">How many previous log files to keep.</param>
    public FileLoggerProvider(string directory, int retainFiles = 5)
    {
        Directory.CreateDirectory(directory);

        _path = Path.Combine(
            directory,
            $"airp-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

        Prune(directory, retainFiles);

        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Full path of the file being written.</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        try
        {
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Nothing useful can be reported at this point; the process is going away.
        }

        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (_disposed)
        {
            return;
        }

        // Dropping a log line is always better than blocking the UI thread on a full queue.
        _queue.TryAdd(line);
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new StringBuilder();

        foreach (var line in _queue.GetConsumingEnumerable())
        {
            buffer.AppendLine(line);

            while (buffer.Length < 32_768 && _queue.TryTake(out var extra))
            {
                buffer.AppendLine(extra);
            }

            try
            {
                await File.AppendAllTextAsync(_path, buffer.ToString(), Encoding.UTF8).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The log is best-effort; a locked or full disk must not take the app down.
            }
            finally
            {
                buffer.Clear();
            }
        }
    }

    private static void Prune(string directory, int retain)
    {
        try
        {
            // Every log in this directory is ours, so matching all of them also retires
            // files written under the previous name rather than leaving them forever.
            var files = new DirectoryInfo(directory)
                .GetFiles("*.log")
                .OrderByDescending(static f => f.CreationTimeUtc)
                .Skip(Math.Max(0, retain));

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping, not a precondition for logging.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(Level(logLevel))
                .Append(' ')
                .Append(category)
                .Append(" — ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            provider.Enqueue(builder.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
