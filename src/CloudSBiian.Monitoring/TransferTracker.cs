using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudSBiian.Monitoring;

public enum TransferDirection
{
    Download,
    Upload,
    Prefetch,
    LocalInstall,
}

public enum TransferOutcome
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A single transfer with speed measured from real byte counts.</summary>
public sealed class TransferInfo
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytes;
    private double _speed;
    private long _lastSampleBytes;
    private double _lastSampleSeconds;

    internal TransferInfo(long id, string accountId, string path, TransferDirection direction, long? totalBytes)
    {
        Id = id;
        AccountId = accountId;
        Path = path;
        Direction = direction;
        TotalBytes = totalBytes;
        StartedAt = DateTimeOffset.Now;
    }

    public long Id { get; }

    public string AccountId { get; }

    public string Path { get; }

    public TransferDirection Direction { get; }

    public long? TotalBytes { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public TransferOutcome Outcome { get; private set; } = TransferOutcome.Running;

    public string? Error { get; private set; }

    public long BytesTransferred => Interlocked.Read(ref _bytes);

    /// <summary>Bytes per second, exponentially smoothed over ~1 s samples.</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                Sample();
                return _speed;
            }
        }
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public double? Progress => TotalBytes is > 0 ? Math.Min(1.0, (double)BytesTransferred / TotalBytes.Value) : null;

    public void Add(long bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        lock (_gate)
        {
            Sample();
        }
    }

    internal void Finish(TransferOutcome outcome, string? error)
    {
        lock (_gate)
        {
            _clock.Stop();
            Outcome = outcome;
            Error = error;
            FinishedAt = DateTimeOffset.Now;
            var secs = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            _speed = BytesTransferred / secs;
        }
    }

    private void Sample()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastSampleSeconds;
        if (dt < 0.5)
        {
            return;
        }

        var bytes = Interlocked.Read(ref _bytes);
        var instant = (bytes - _lastSampleBytes) / dt;
        _speed = _lastSampleSeconds == 0 ? instant : (_speed * 0.6) + (instant * 0.4);
        _lastSampleBytes = bytes;
        _lastSampleSeconds = now;
    }
}

/// <summary>
/// Tracks active and recent transfers and keeps a per-second throughput history for charts.
/// </summary>
public sealed class TransferTracker
{
    private const int HistoryCapacity = 300;
    private readonly ConcurrentDictionary<long, TransferInfo> _active = new();
    private readonly ConcurrentQueue<TransferInfo> _recent = new();
    private readonly ConcurrentDictionary<string, AccountSpeedStats> _accountStats = new();
    private readonly MetricsRegistry _metrics;
    private long _nextId;

    public TransferTracker(MetricsRegistry metrics) => _metrics = metrics;

    public event EventHandler<TransferInfo>? TransferStarted;

    public event EventHandler<TransferInfo>? TransferFinished;

    public IReadOnlyList<TransferInfo> Active => _active.Values.OrderBy(t => t.StartedAt).ToList();

    public IReadOnlyList<TransferInfo> Recent => _recent.Reverse().ToList();

    public TransferHandle Begin(string accountId, string path, TransferDirection direction, long? totalBytes)
    {
        var info = new TransferInfo(Interlocked.Increment(ref _nextId), accountId, path, direction, totalBytes);
        _active[info.Id] = info;
        TransferStarted?.Invoke(this, info);
        return new TransferHandle(this, info);
    }

    internal void Complete(TransferInfo info, TransferOutcome outcome, string? error)
    {
        if (!_active.TryRemove(info.Id, out _))
        {
            return;
        }

        info.Finish(outcome, error);
        _recent.Enqueue(info);
        while (_recent.Count > HistoryCapacity && _recent.TryDequeue(out _))
        {
        }

        var counter = info.Direction == TransferDirection.Upload ? MetricNames.BytesUploaded : MetricNames.BytesDownloaded;
        _metrics.Increment(counter, info.BytesTransferred);
        if (outcome == TransferOutcome.Failed)
        {
            _metrics.Increment(MetricNames.FailedOperations);
        }

        if (outcome == TransferOutcome.Completed && info.BytesTransferred >= 256 * 1024)
        {
            _accountStats.GetOrAdd(info.AccountId, _ => new AccountSpeedStats()).Add(info.BytesTransferred, info.Elapsed);
        }

        TransferFinished?.Invoke(this, info);
    }

    /// <summary>Current aggregate speed of all active transfers in the given direction(s).</summary>
    public double CurrentSpeed(params TransferDirection[] directions) =>
        _active.Values.Where(t => directions.Length == 0 || directions.Contains(t.Direction)).Sum(t => t.BytesPerSecond);

    /// <summary>
    /// Average measured download speed for an account, or null when there is not enough data.
    /// Used for honest ETA estimates.
    /// </summary>
    public double? MeasuredSpeed(string accountId) =>
        _accountStats.TryGetValue(accountId, out var s) ? s.Average : null;
}

public sealed class AccountSpeedStats
{
    private readonly object _gate = new();
    private long _bytes;
    private double _seconds;

    public void Add(long bytes, TimeSpan elapsed)
    {
        lock (_gate)
        {
            // Exponential decay so recent transfers dominate.
            _bytes = (long)(_bytes * 0.8) + bytes;
            _seconds = (_seconds * 0.8) + Math.Max(0.001, elapsed.TotalSeconds);
        }
    }

    public double? Average
    {
        get
        {
            lock (_gate)
            {
                return _seconds <= 0 ? null : _bytes / _seconds;
            }
        }
    }
}

/// <summary>Disposable wrapper: disposing without Complete/Fail marks the transfer cancelled.</summary>
public sealed class TransferHandle : IDisposable, IProgress<long>
{
    private readonly TransferTracker _tracker;
    private int _done;

    internal TransferHandle(TransferTracker tracker, TransferInfo info)
    {
        _tracker = tracker;
        Info = info;
    }

    public TransferInfo Info { get; }

    public void Add(long bytes) => Info.Add(bytes);

    /// <summary>IProgress implementation: reports cumulative bytes.</summary>
    public void Report(long cumulativeBytes)
    {
        var delta = cumulativeBytes - Info.BytesTransferred;
        if (delta > 0)
        {
            Info.Add(delta);
        }
    }

    public void Complete() => Finish(TransferOutcome.Completed, null);

    public void Fail(string error) => Finish(TransferOutcome.Failed, error);

    public void Dispose() => Finish(TransferOutcome.Cancelled, null);

    private void Finish(TransferOutcome outcome, string? error)
    {
        if (Interlocked.Exchange(ref _done, 1) == 0)
        {
            _tracker.Complete(Info, outcome, error);
        }
    }
}
