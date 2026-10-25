using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.Monitoring;

/// <summary>
/// Persists warnings/errors (and selected info events) to SQLite so the Logs page and
/// dashboard can show history across restarts. Wraps the file log.
/// </summary>
public sealed class ActivityLog : IAppLog
{
    private readonly AppDatabase _db;
    private readonly IAppLog _inner;

    public ActivityLog(AppDatabase db, IAppLog inner)
    {
        _db = db;
        _inner = inner;
    }

    public event EventHandler<LogEntry>? Recorded;

    public void Write(LogLevel level, string category, string message, Exception? ex = null, string? accountId = null)
    {
        _inner.Write(level, category, message, ex, accountId);
        if (level < LogLevel.Info)
        {
            return;
        }

        var text = Core.Security.SecretRedactor.Redact(ex == null ? message : $"{message} ({ex.Message})");
        try
        {
            _db.Execute(
                "INSERT INTO activity_log(ts, level, category, account_id, message) VALUES(@ts,@level,@cat,@acc,@msg)",
                ("@ts", Clock.NowMs()),
                ("@level", level.ToString()),
                ("@cat", category),
                ("@acc", accountId),
                ("@msg", text));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Never let logging failures propagate.
        }

        Recorded?.Invoke(this, new LogEntry(DateTimeOffset.Now, level, category, text, accountId));
    }

    public IReadOnlyList<LogEntry> Query(LogLevel minimum, string? accountId = null, int limit = 500) =>
        _db.Query(
            "SELECT ts, level, category, account_id, message FROM activity_log WHERE (@acc IS NULL OR account_id=@acc) ORDER BY id DESC LIMIT @limit",
            r => new LogEntry(
                Clock.FromMs(r.GetInt64(0)).ToLocalTime(),
                Enum.TryParse<LogLevel>(r.GetString(1), out var l) ? l : LogLevel.Info,
                r.GetString(2),
                r.GetString(4),
                AppDatabase.GetStringOrNull(r, 3)),
            ("@acc", accountId),
            ("@limit", limit * 2))
        .Where(e => e.Level >= minimum)
        .Take(limit)
        .ToList();

    public int CountSince(LogLevel level, DateTimeOffset since) =>
        (int)(_db.Scalar<long?>(
            "SELECT COUNT(*) FROM activity_log WHERE level=@level AND ts>=@since",
            ("@level", level.ToString()),
            ("@since", since.ToUnixTimeMilliseconds())) ?? 0);

    public void Prune(TimeSpan keep) =>
        _db.Execute("DELETE FROM activity_log WHERE ts < @cut", ("@cut", Clock.NowMs() - (long)keep.TotalMilliseconds));
}

/// <summary>Samples throughput and cache hit rate every second for dashboard charts.</summary>
public sealed class MetricsSampler : IDisposable
{
    private readonly MetricsRegistry _metrics;
    private readonly TransferTracker _transfers;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Queue<MetricSample> _samples = new();
    private long _lastDown;
    private long _lastUp;
    private long _lastHits;
    private long _lastMisses;

    public MetricsSampler(MetricsRegistry metrics, TransferTracker transfers, int capacity = 120)
    {
        _metrics = metrics;
        _transfers = transfers;
        Capacity = capacity;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public int Capacity { get; }

    public IReadOnlyList<MetricSample> Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples.ToArray();
            }
        }
    }

    private void Tick()
    {
        // Completed-transfer counters plus bytes of in-flight transfers give the real rate.
        var activeDown = _transfers.Active.Where(t => t.Direction != TransferDirection.Upload).Sum(t => t.BytesTransferred);
        var activeUp = _transfers.Active.Where(t => t.Direction == TransferDirection.Upload).Sum(t => t.BytesTransferred);
        var down = _metrics.Get(MetricNames.BytesDownloaded) + activeDown;
        var up = _metrics.Get(MetricNames.BytesUploaded) + activeUp;
        var hits = _metrics.Get(MetricNames.CacheHits);
        var misses = _metrics.Get(MetricNames.CacheMisses);

        var sample = new MetricSample(
            DateTimeOffset.Now,
            Math.Max(0, down - _lastDown),
            Math.Max(0, up - _lastUp),
            hits - _lastHits,
            misses - _lastMisses);
        _lastDown = down;
        _lastUp = up;
        _lastHits = hits;
        _lastMisses = misses;

        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > Capacity)
            {
                _samples.Dequeue();
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}

public sealed record MetricSample(DateTimeOffset Time, long DownloadBytes, long UploadBytes, long Hits, long Misses);
