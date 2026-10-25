using System.Collections.Concurrent;
using System.Diagnostics;
using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.PredictionEngine;

/// <summary>Per-handle read pattern tracker (sequential vs random).</summary>
public sealed class ReadPattern
{
    internal long LastEnd = -1;
    internal int ConsecutiveSequential;
    internal int Sequential;
    internal int Random;
    internal long Bytes;
    internal int LastReadAheadChunk = -1;
}

public sealed record PrefetchRequest(string AccountId, string Path, IReadOnlyList<int>? Chunks, FetchPriority Priority, string Reason);

public sealed record PredictionStats(
    bool Enabled,
    PredictionAggressiveness Aggressiveness,
    int QueueLength,
    long RequestsIssued,
    long RequestsDropped,
    long BytesPrefetchedThisHour,
    long HourlyBudgetBytes,
    long ProfiledFiles,
    long CoAccessPairs,
    long TraceChunks);

/// <summary>
/// Heuristic access prediction (no machine learning): sequential read-ahead, co-access
/// statistics ("B is usually opened after A") and recorded launch traces. All prefetch work
/// goes through the cache's priority scheduler at P1-P3, so active reads (P0) always win,
/// and it respects the cache budget, the free-space floor and the prefetch bandwidth limit.
/// </summary>
public sealed class PredictionService : IDisposable
{
    private readonly CacheService _cache;
    private readonly IProviderResolver _providers;
    private readonly Func<AppSettings> _settings;
    private readonly IAppLog _log;
    private readonly ConcurrentDictionary<string, LinkedList<(string Path, long At)>> _recentOpens = new();
    private readonly object _queueGate = new();
    private readonly PriorityQueue<PrefetchRequest, (int, long)> _queue = new();
    private readonly HashSet<string> _queued = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Stopwatch> _activeTraces = new();
    private readonly Task _worker;
    private long _sequence;
    private long _issued;
    private long _dropped;
    private long _hourBytes;
    private DateTime _hourStart = DateTime.UtcNow;

    public PredictionService(CacheService cache, AccessProfileStore profiles, IProviderResolver providers, Func<AppSettings> settings, IAppLog? log = null)
    {
        _cache = cache;
        Profiles = profiles;
        _providers = providers;
        _settings = settings;
        _log = log ?? NullLog.Instance;
        _cache.ChunkAccessed += OnChunkAccessed;
        _worker = Task.Run(() => WorkerAsync(_stop.Token));
    }

    public AccessProfileStore Profiles { get; }

    private AppSettings Settings => _settings();

    private bool Enabled => Settings.PredictionEnabled && Settings.PredictionAggressiveness != PredictionAggressiveness.Off;

    public long HourlyBudget => Settings.PredictionAggressiveness switch
    {
        PredictionAggressiveness.Conservative => 512L * 1024 * 1024,
        PredictionAggressiveness.Balanced => 2L * 1024 * 1024 * 1024,
        PredictionAggressiveness.Aggressive => 8L * 1024 * 1024 * 1024,
        _ => 0,
    };

    private int ReadAheadChunks => Settings.PredictionAggressiveness switch
    {
        PredictionAggressiveness.Conservative => 1,
        PredictionAggressiveness.Balanced => 2,
        PredictionAggressiveness.Aggressive => 4,
        _ => 0,
    };

    /// <summary>Largest file prefetched completely on a co-access prediction.</summary>
    private long WholeFileLimit => Settings.PredictionAggressiveness switch
    {
        PredictionAggressiveness.Aggressive => 64L * 1024 * 1024,
        PredictionAggressiveness.Balanced => 16L * 1024 * 1024,
        _ => 0,
    };

    // ------------------------------------------------------------------ events from the drive

    public void OnDirectoryListed(string accountId, string path)
    {
        if (!Enabled)
        {
            return;
        }

        Safe(() => Profiles.RecordFolderVisit(accountId, CloudPath.Normalize(path)));
    }

    public void OnFileOpened(string accountId, CloudEntry entry)
    {
        var path = CloudPath.Normalize(entry.Path);
        Safe(() => Profiles.RecordOpen(accountId, path, entry.Size));
        if (!Enabled)
        {
            return;
        }

        var now = Environment.TickCount64;
        var recent = _recentOpens.GetOrAdd(accountId, _ => new LinkedList<(string, long)>());
        List<string> previous;
        lock (recent)
        {
            while (recent.First != null && now - recent.First.Value.At > 60_000)
            {
                recent.RemoveFirst();
            }

            previous = recent.Where(r => r.Path != path).Select(r => r.Path).Distinct().TakeLast(8).ToList();
            recent.AddLast((path, now));
            while (recent.Count > 32)
            {
                recent.RemoveFirst();
            }
        }

        Safe(() =>
        {
            foreach (var p in previous)
            {
                Profiles.RecordCoAccess(accountId, p, path);
            }
        });

        foreach (var (follower, probability, count) in Profiles.Followers(accountId, path, 8))
        {
            if (count < 2)
            {
                continue;
            }

            var priority = probability >= 0.6 ? FetchPriority.P1 : probability >= 0.3 ? FetchPriority.P2 : FetchPriority.P3;
            if (priority == FetchPriority.P3 && Settings.PredictionAggressiveness != PredictionAggressiveness.Aggressive)
            {
                continue;
            }

            Enqueue(new PrefetchRequest(accountId, follower, null, priority, $"co-access after {CloudPath.GetName(path)} (p={probability:F2})"));
        }
    }

    /// <summary>Called for every read on a handle; detects sequential patterns and schedules read-ahead.</summary>
    public void OnRead(string accountId, CloudEntry entry, ReadPattern pattern, long offset, int length)
    {
        var sequential = pattern.LastEnd >= 0 && Math.Abs(offset - pattern.LastEnd) <= 64 * 1024;
        if (sequential)
        {
            pattern.Sequential++;
            pattern.ConsecutiveSequential++;
        }
        else if (pattern.LastEnd >= 0)
        {
            pattern.Random++;
            pattern.ConsecutiveSequential = 0;
        }

        pattern.LastEnd = offset + length;
        pattern.Bytes += length;

        if (!Enabled || pattern.ConsecutiveSequential < 2)
        {
            return;
        }

        var chunkSize = _cache.ChunkSize;
        var current = (int)(pattern.LastEnd / chunkSize);
        var lastChunk = entry.Size == 0 ? 0 : (int)((entry.Size - 1) / chunkSize);
        if (current <= pattern.LastReadAheadChunk - 1)
        {
            return;
        }

        var chunks = Enumerable.Range(current + 1, ReadAheadChunks).Where(c => c <= lastChunk).ToList();
        if (chunks.Count == 0)
        {
            return;
        }

        pattern.LastReadAheadChunk = chunks[^1];
        Enqueue(new PrefetchRequest(accountId, entry.Path, chunks, FetchPriority.P1, "sequential read-ahead"));
    }

    public void OnFileClosed(string accountId, CloudEntry entry, ReadPattern pattern)
    {
        if (pattern.Bytes > 0)
        {
            Safe(() => Profiles.RecordReads(accountId, CloudPath.Normalize(entry.Path), pattern.Bytes, pattern.Sequential, pattern.Random));
        }
    }

    // ------------------------------------------------------------------ launch traces

    /// <summary>Starts recording which chunks are read (e.g. while a game launches) under <paramref name="traceId"/>.</summary>
    public void StartTrace(string traceId)
    {
        _activeTraces[traceId] = Stopwatch.StartNew();
        _log.Info("prediction", $"Recording access trace '{traceId}'.");
    }

    public void StopTrace(string traceId)
    {
        if (_activeTraces.TryRemove(traceId, out _))
        {
            _log.Info("prediction", $"Stopped access trace '{traceId}'.");
        }
    }

    public bool IsTracing(string traceId) => _activeTraces.ContainsKey(traceId);

    private void OnChunkAccessed(object? sender, ChunkAccess access)
    {
        if (access.Priority != FetchPriority.P0 || _activeTraces.IsEmpty)
        {
            return;
        }

        foreach (var (traceId, clock) in _activeTraces)
        {
            Safe(() => Profiles.AddTraceChunk(traceId, access.AccountId, access.Path, access.ChunkIndex, clock.ElapsedMilliseconds));
        }
    }

    // ------------------------------------------------------------------ queue

    public void Enqueue(PrefetchRequest request)
    {
        if (!Enabled)
        {
            return;
        }

        var key = $"{request.AccountId}|{request.Path}|{(request.Chunks == null ? "*" : string.Join(',', request.Chunks))}";
        lock (_queueGate)
        {
            if (_queue.Count >= 512 || !_queued.Add(key))
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            _queue.Enqueue(request, ((int)request.Priority, Interlocked.Increment(ref _sequence)));
        }

        _signal.Release();
    }

    public PredictionStats GetStats()
    {
        var (files, pairs, trace) = Profiles.Counts();
        int queueLength;
        lock (_queueGate)
        {
            queueLength = _queue.Count;
        }

        return new PredictionStats(Enabled, Settings.PredictionAggressiveness, queueLength, Interlocked.Read(ref _issued), Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _hourBytes), HourlyBudget, files, pairs, trace);
    }

    public void ClearQueue()
    {
        lock (_queueGate)
        {
            _queue.Clear();
            _queued.Clear();
        }
    }

    private async Task WorkerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(stop).ConfigureAwait(false);
                PrefetchRequest? request;
                lock (_queueGate)
                {
                    if (!_queue.TryDequeue(out request, out _))
                    {
                        continue;
                    }
                }

                try
                {
                    await ProcessAsync(request, stop).ConfigureAwait(false);
                }
                finally
                {
                    lock (_queueGate)
                    {
                        _queued.Remove($"{request.AccountId}|{request.Path}|{(request.Chunks == null ? "*" : string.Join(',', request.Chunks))}");
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Debug("prediction", $"Prefetch skipped: {ex.Message}");
            }
        }
    }

    private async Task ProcessAsync(PrefetchRequest request, CancellationToken stop)
    {
        if (!Enabled)
        {
            return;
        }

        if (DateTime.UtcNow - _hourStart > TimeSpan.FromHours(1))
        {
            _hourStart = DateTime.UtcNow;
            Interlocked.Exchange(ref _hourBytes, 0);
        }

        if (Interlocked.Read(ref _hourBytes) >= HourlyBudget)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        var provider = _providers.TryGetProvider(request.AccountId);
        if (provider == null)
        {
            return;
        }

        var entry = await _cache.Metadata.StatAsync(request.AccountId, provider, request.Path, stop).ConfigureAwait(false);
        if (entry == null || entry.IsDirectory || entry.Size == 0)
        {
            return;
        }

        IReadOnlyList<int> chunks;
        if (request.Chunks != null)
        {
            chunks = request.Chunks;
        }
        else if (entry.Size <= WholeFileLimit)
        {
            chunks = Enumerable.Range(0, (int)((entry.Size - 1) / _cache.ChunkSize) + 1).ToList();
        }
        else
        {
            // Large file: only the head chunk, which most programs read first (headers, indexes).
            chunks = new[] { 0 };
        }

        Interlocked.Increment(ref _issued);
        var bytes = await _cache.PrefetchAsync(request.AccountId, entry, chunks, request.Priority, stop).ConfigureAwait(false);
        Interlocked.Add(ref _hourBytes, bytes);
        if (bytes > 0)
        {
            _log.Debug("prediction", $"Prefetched {bytes / 1024} KiB of {request.Path} ({request.Reason}).", request.AccountId);
        }
    }

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Debug("prediction", $"Profile update failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cache.ChunkAccessed -= OnChunkAccessed;
        _stop.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
