using System.Collections.Concurrent;
using System.Text;
using CloudSBiian.Core.Security;

namespace CloudSBiian.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? AccountId);

public interface IAppLog
{
    void Write(LogLevel level, string category, string message, Exception? ex = null, string? accountId = null);
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string category, string message, string? accountId = null) =>
        log.Write(LogLevel.Debug, category, message, null, accountId);

    public static void Info(this IAppLog log, string category, string message, string? accountId = null) =>
        log.Write(LogLevel.Info, category, message, null, accountId);

    public static void Warn(this IAppLog log, string category, string message, Exception? ex = null, string? accountId = null) =>
        log.Write(LogLevel.Warning, category, message, ex, accountId);

    public static void Error(this IAppLog log, string category, string message, Exception? ex = null, string? accountId = null) =>
        log.Write(LogLevel.Error, category, message, ex, accountId);
}

/// <summary>Discards everything. Default for components constructed without a log.</summary>
public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string category, string message, Exception? ex = null, string? accountId = null)
    {
    }
}

/// <summary>
/// File logger with daily files, size cap and redaction. Also keeps a bounded in-memory
/// ring buffer for the Logs page and raises <see cref="EntryWritten"/>.
/// </summary>
public sealed class AppLog : IAppLog, IDisposable
{
    private const int RingCapacity = 3000;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 20000);
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly Thread _writer;

    public AppLog(string directory, LogLevel minimumLevel = LogLevel.Info)
    {
        _directory = directory;
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "CloudSBiian log writer" };
        _writer.Start();
        PruneOldFiles();
    }

    public LogLevel MinimumLevel { get; set; }

    public event EventHandler<LogEntry>? EntryWritten;

    public IReadOnlyList<LogEntry> Recent => _ring.ToArray();

    public string CurrentFile => Path.Combine(_directory, $"cloudsbiian-{DateTime.Now:yyyyMMdd}.log");

    public void Write(LogLevel level, string category, string message, Exception? ex = null, string? accountId = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var text = SecretRedactor.Redact(ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");
        var entry = new LogEntry(DateTimeOffset.Now, level, category, text, accountId);
        _ring.Enqueue(entry);
        while (_ring.Count > RingCapacity && _ring.TryDequeue(out _))
        {
        }

        var line = new StringBuilder()
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(category);
        if (accountId != null)
        {
            line.Append(" {").Append(accountId).Append('}');
        }

        line.Append(": ").Append(text);
        if (ex != null && level >= LogLevel.Error)
        {
            line.AppendLine().Append(SecretRedactor.Redact(ex.ToString()));
        }

        _queue.TryAdd(line.ToString());
        EntryWritten?.Invoke(this, entry);
    }

    private void WriterLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var file = CurrentFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Move(file, Path.Combine(_directory, $"cloudsbiian-{DateTime.Now:yyyyMMdd-HHmmss}.log"), overwrite: true);
                }

                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_directory).GetFiles("cloudsbiian-*.log"))
            {
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14))
                {
                    f.Delete();
                }
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }
}
