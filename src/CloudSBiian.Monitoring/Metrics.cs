using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudSBiian.Monitoring;

/// <summary>Well-known metric names so producers and the dashboard agree.</summary>
public static class MetricNames
{
    public const string CacheHits = "cache.hits";
    public const string CacheMisses = "cache.misses";
    public const string CacheBytesServed = "cache.bytes_served";
    public const string BytesDownloaded = "transfer.bytes_downloaded";
    public const string BytesUploaded = "transfer.bytes_uploaded";
    public const string PrefetchIssued = "prefetch.chunks_issued";
    public const string PrefetchBytes = "prefetch.bytes";
    public const string PrefetchUseful = "prefetch.chunks_used";
    public const string PrefetchCancelled = "prefetch.cancelled";
    public const string FailedOperations = "ops.failed";
    public const string CacheEvictions = "cache.evictions";
    public const string CacheCorruptChunks = "cache.corrupt_chunks";
    public const string ReadLatency = "latency.read";
    public const string ListLatency = "latency.list";
    public const string ProviderLatency = "latency.provider_fetch";
}

/// <summary>
/// Thread-safe counters and latency samples. Values only come from real operations;
/// nothing here is synthesized.
/// </summary>
public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, LatencyWindow> _latencies = new();

    public void Increment(string name, long by = 1) => _counters.AddOrUpdate(name, by, (_, v) => v + by);

    public long Get(string name) => _counters.TryGetValue(name, out var v) ? v : 0;

    public IReadOnlyDictionary<string, long> Snapshot() => new Dictionary<string, long>(_counters);

    public void RecordLatency(string name, TimeSpan elapsed) =>
        _latencies.GetOrAdd(name, _ => new LatencyWindow(512)).Add(elapsed.TotalMilliseconds);

    /// <summary>Returns null when no samples were recorded yet (UI shows N/A).</summary>
    public LatencySummary? GetLatency(string name) =>
        _latencies.TryGetValue(name, out var w) ? w.Summarize() : null;

    public IReadOnlyDictionary<string, LatencySummary> LatencySnapshot()
    {
        var result = new Dictionary<string, LatencySummary>();
        foreach (var (k, w) in _latencies)
        {
            var s = w.Summarize();
            if (s != null)
            {
                result[k] = s;
            }
        }

        return result;
    }

    public double? CacheHitRate()
    {
        var hits = Get(MetricNames.CacheHits);
        var misses = Get(MetricNames.CacheMisses);
        var total = hits + misses;
        return total == 0 ? null : (double)hits / total;
    }

    public double? PrefetchAccuracy()
    {
        var issued = Get(MetricNames.PrefetchIssued);
        return issued == 0 ? null : Math.Min(1.0, (double)Get(MetricNames.PrefetchUseful) / issued);
    }

    public void Reset()
    {
        _counters.Clear();
        _latencies.Clear();
    }

    public readonly struct Timer : IDisposable
    {
        private readonly MetricsRegistry _registry;
        private readonly string _name;
        private readonly long _start;

        internal Timer(MetricsRegistry registry, string name)
        {
            _registry = registry;
            _name = name;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose() => _registry.RecordLatency(_name, Stopwatch.GetElapsedTime(_start));
    }

    public Timer Time(string name) => new(this, name);
}

public sealed record LatencySummary(int Samples, double AverageMs, double P50Ms, double P95Ms, double MaxMs);

internal sealed class LatencyWindow
{
    private readonly double[] _samples;
    private readonly object _gate = new();
    private int _count;
    private int _next;

    public LatencyWindow(int capacity) => _samples = new double[capacity];

    public void Add(double ms)
    {
        lock (_gate)
        {
            _samples[_next] = ms;
            _next = (_next + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
        }
    }

    public LatencySummary? Summarize()
    {
        double[] copy;
        lock (_gate)
        {
            if (_count == 0)
            {
                return null;
            }

            copy = new double[_count];
            Array.Copy(_samples, copy, _count);
        }

        Array.Sort(copy);
        return new LatencySummary(
            copy.Length,
            copy.Average(),
            copy[(int)(copy.Length * 0.5)],
            copy[Math.Min(copy.Length - 1, (int)(copy.Length * 0.95))],
            copy[^1]);
    }
}
