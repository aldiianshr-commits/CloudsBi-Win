using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.Core.Storage;
using CloudSBiian.Monitoring;

namespace CloudSBiian.CacheEngine;

public sealed record ChunkAccess(string AccountId, string Path, int ChunkIndex, int ChunkSize, bool Hit, FetchPriority Priority);

public sealed record CacheStatus(
    long BudgetBytes,
    long PhysicalBytes,
    long MinFreeBytes,
    long? DiskFreeBytes,
    long StagingBytes,
    string CacheDirectory,
    IReadOnlyList<AccountCacheUsage> PerAccount);

/// <summary>
/// Read-through block cache with budget enforcement, pinning, exclusions and prioritized
/// downloads. This is the only path through which the drive and the prefetcher read cloud data.
/// </summary>
public sealed class CacheService : IDisposable
{
    private readonly ConcurrentDictionary<(string, string, int), Lazy<Task<byte[]>>> _inflight = new();
    private readonly ConcurrentDictionary<string, PriorityLimiter> _limiters = new();
    private readonly ConcurrentDictionary<(string, string), SequentialFetch> _sequential = new();
    private readonly IProviderResolver _providers;
    private readonly AppDatabase _db;
    private readonly MetricsRegistry _metrics;
    private readonly TransferTracker _transfers;
    private readonly IAppLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly BandwidthLimiter _prefetchBandwidth;
    private readonly Timer _maintenance;
    private readonly object _pinGate = new();
    private Dictionary<string, List<string>> _pins = new();
    private List<(string? Account, Regex Regex, string Pattern, long Id)> _exclusions = new();

    public CacheService(
        string cacheDirectory,
        AppDatabase db,
        IProviderResolver providers,
        MetricsRegistry metrics,
        TransferTracker transfers,
        Func<AppSettings> settings,
        IAppLog? log = null)
    {
        CacheDirectory = Path.GetFullPath(cacheDirectory);
        _db = db;
        _providers = providers;
        _metrics = metrics;
        _transfers = transfers;
        _settings = settings;
        _log = log ?? NullLog.Instance;
        Store = new ChunkStore(Path.Combine(CacheDirectory, "blocks"), db, metrics, () => _settings().VerifyChunksOnRead, _log);
        Metadata = new MetadataCache(db, metrics, () => TimeSpan.FromSeconds(Math.Clamp(_settings().MetadataTtlSeconds, 1, 3600)));
        _prefetchBandwidth = new BandwidthLimiter(() => _settings().PrefetchBandwidthLimitBytesPerSecond);
        ReloadPins();
        ReloadExclusions();
        var removed = Store.CleanupTempFiles();
        if (removed > 0)
        {
            _log.Info("cache", $"Recovered from interrupted writes: removed {removed} temp files.");
        }

        _maintenance = new Timer(_ => Maintenance(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public string CacheDirectory { get; }

    public ChunkStore Store { get; }

    public MetadataCache Metadata { get; }

    /// <summary>Directory where per-account staging (pending upload) files live.</summary>
    public string StagingRoot => Path.Combine(CacheDirectory, "staging");

    /// <summary>Raised for every chunk served (hit or miss). Used for prediction traces.</summary>
    public event EventHandler<ChunkAccess>? ChunkAccessed;

    public event EventHandler<string>? Warning;

    public int ChunkSize => _settings().ChunkSizeBytes;

    private PriorityLimiter LimiterFor(string accountId)
    {
        var limiter = _limiters.GetOrAdd(accountId, _ =>
        {
            var l = new PriorityLimiter(_settings().MaxConcurrentDownloadsPerAccount);
            l.Preempted += (_, _) => _metrics.Increment(MetricNames.PrefetchCancelled);
            return l;
        });
        limiter.MaxConcurrency = _settings().MaxConcurrentDownloadsPerAccount;
        return limiter;
    }

    public (int Running, int Waiting) QueueDepth(string accountId) =>
        _limiters.TryGetValue(accountId, out var l) ? (l.Running, l.Waiting) : (0, 0);

    // ------------------------------------------------------------------ reads

    /// <summary>
    /// Reads <paramref name="buffer"/>.Length bytes at <paramref name="offset"/> of a cloud file through the cache.
    /// Returns the number of bytes read (0 at end of file).
    /// </summary>
    public async Task<int> ReadAsync(string accountId, CloudEntry entry, long offset, Memory<byte> buffer, FetchPriority priority, CancellationToken ct)
    {
        if (entry.IsDirectory)
        {
            throw new CloudProviderException(CloudErrorKind.InvalidPath, "Cannot read a directory.");
        }

        if (offset >= entry.Size || buffer.Length == 0)
        {
            return 0;
        }

        var sw = Stopwatch.StartNew();
        var length = (int)Math.Min(buffer.Length, entry.Size - offset);
        var provider = _providers.TryGetProvider(accountId);

        if (IsExcluded(accountId, entry.Path))
        {
            var direct = await ReadDirectAsync(accountId, provider, entry, offset, buffer[..length], ct).ConfigureAwait(false);
            _metrics.RecordLatency(MetricNames.ReadLatency, sw.Elapsed);
            return direct;
        }

        var chunkSize = ChunkSize;
        var record = Store.EnsureFile(accountId, entry, chunkSize);
        var first = (int)(offset / chunkSize);
        var last = (int)((offset + length - 1) / chunkSize);
        var written = 0;
        for (var index = first; index <= last; index++)
        {
            var chunk = await GetChunkAsync(accountId, provider, entry, record, index, priority, ct).ConfigureAwait(false);
            var chunkStart = (long)index * chunkSize;
            var from = (int)Math.Max(0, offset - chunkStart);
            var take = Math.Min(chunk.Length - from, length - written);
            if (take <= 0)
            {
                break;
            }

            chunk.AsSpan(from, take).CopyTo(buffer.Span[written..]);
            written += take;
        }

        _metrics.Increment(MetricNames.CacheBytesServed, written);
        _metrics.RecordLatency(MetricNames.ReadLatency, sw.Elapsed);
        return written;
    }

    /// <summary>Makes sure the given chunks are cached. Used by prediction and Optimize &amp; Prepare.</summary>
    public async Task<long> PrefetchAsync(string accountId, CloudEntry entry, IEnumerable<int> chunkIndexes, FetchPriority priority, CancellationToken ct)
    {
        if (entry.IsDirectory || entry.Size == 0 || IsExcluded(accountId, entry.Path))
        {
            return 0;
        }

        var provider = _providers.TryGetProvider(accountId);
        if (provider == null)
        {
            return 0;
        }

        var chunkSize = ChunkSize;
        var record = Store.EnsureFile(accountId, entry, chunkSize);
        var maxIndex = (int)((entry.Size - 1) / chunkSize);
        long bytes = 0;
        foreach (var index in chunkIndexes.Where(i => i >= 0 && i <= maxIndex).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (Store.Contains(accountId, record.FileKey, index))
            {
                continue;
            }

            if (!HasRoomFor(chunkSize))
            {
                break;
            }

            try
            {
                var data = await GetChunkAsync(accountId, provider, entry, record, index, priority, ct).ConfigureAwait(false);
                bytes += data.Length;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Preempted by an active read: stop this batch, the predictor will retry later.
                break;
            }
        }

        return bytes;
    }

    /// <summary>Indexes of chunks of the file not yet cached.</summary>
    public IReadOnlyList<int> MissingChunks(string accountId, CloudEntry entry)
    {
        var chunkSize = ChunkSize;
        var record = Store.EnsureFile(accountId, entry, chunkSize);
        var cached = Store.CachedChunks(accountId, record.FileKey);
        var count = entry.Size == 0 ? 0 : (int)((entry.Size - 1) / chunkSize) + 1;
        return Enumerable.Range(0, count).Where(i => !cached.Contains(i)).ToList();
    }

    private async Task<byte[]> GetChunkAsync(string accountId, ICloudStorageProvider? provider, CloudEntry entry, CachedFileRecord record, int index, FetchPriority priority, CancellationToken ct)
    {
        var chunkSize = record.ChunkSize;
        var expected = (int)Math.Min(chunkSize, entry.Size - ((long)index * chunkSize));
        for (var attempt = 0; ; attempt++)
        {
            var cached = Store.TryRead(accountId, record.FileKey, index, expected);
            if (cached != null)
            {
                if (priority == FetchPriority.P0)
                {
                    _metrics.Increment(MetricNames.CacheHits);
                }

                ChunkAccessed?.Invoke(this, new ChunkAccess(accountId, record.Path, index, chunkSize, true, priority));
                return cached;
            }

            if (provider == null)
            {
                throw new CloudProviderException(CloudErrorKind.Network, "Account is offline and the data is not cached.");
            }

            if (priority == FetchPriority.P0 && attempt == 0)
            {
                _metrics.Increment(MetricNames.CacheMisses);
            }

            var key = (accountId, record.FileKey, index);
            var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(
                () => FetchChunkAsync(accountId, provider, entry, record, index, expected, priority)));
            try
            {
                var data = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
                ChunkAccessed?.Invoke(this, new ChunkAccess(accountId, record.Path, index, chunkSize, false, priority));
                return data;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3 && priority < FetchPriority.P2)
            {
                // We joined a prefetch that got preempted; fetch again at our own priority.
            }
            catch (CloudProviderException ex) when (ex.IsTransient && priority == FetchPriority.P0 && attempt < 3)
            {
                var delay = ex.RetryAfter ?? TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt));
                _log.Debug("cache", $"Retrying chunk after {ex.Kind} ({delay.TotalMilliseconds:F0} ms).", accountId);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> FetchChunkAsync(string accountId, ICloudStorageProvider provider, CloudEntry entry, CachedFileRecord record, int index, int expected, FetchPriority priority)
    {
        var key = (accountId, record.FileKey, index);
        try
        {
            if ((provider.Capabilities & ProviderCapabilities.RangeRead) == 0)
            {
                return await FetchSequentialAsync(accountId, provider, entry, record, index, expected, priority).ConfigureAwait(false);
            }

            using var lease = await LimiterFor(accountId).AcquireAsync(priority, CancellationToken.None).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, timeout.Token);
            var direction = priority == FetchPriority.P0 ? TransferDirection.Download : TransferDirection.Prefetch;
            using var transfer = _transfers.Begin(accountId, entry.Path, direction, expected);
            var sw = Stopwatch.StartNew();
            var data = new byte[expected];
            try
            {
                await using var stream = await provider.OpenReadAsync(entry.Path, (long)index * record.ChunkSize, expected, linked.Token).ConfigureAwait(false);
                var total = 0;
                while (total < expected)
                {
                    var slice = Math.Min(256 * 1024, expected - total);
                    if (priority >= FetchPriority.P1)
                    {
                        await _prefetchBandwidth.ConsumeAsync(slice, linked.Token).ConfigureAwait(false);
                    }

                    var n = await stream.ReadAsync(data.AsMemory(total, slice), linked.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    total += n;
                    transfer.Add(n);
                }

                if (total != expected)
                {
                    throw new CloudProviderException(CloudErrorKind.Network, $"Short read from backend ({total}/{expected} bytes). The remote file may have changed.");
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                transfer.Fail("timeout");
                throw new CloudProviderException(CloudErrorKind.Timeout, $"Download of {entry.Name} timed out.");
            }
            catch (CloudProviderException ex)
            {
                transfer.Fail(ex.Message);
                _providers.ReportFailure(accountId, ex);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                transfer.Fail(ex.Message);
                throw new CloudProviderException(CloudErrorKind.Network, ex.Message, ex);
            }

            transfer.Complete();
            _metrics.RecordLatency(MetricNames.ProviderLatency, sw.Elapsed);
            if (priority >= FetchPriority.P1)
            {
                _metrics.Increment(MetricNames.PrefetchIssued);
                _metrics.Increment(MetricNames.PrefetchBytes, data.Length);
            }

            StoreChunk(accountId, record, index, data, priority == FetchPriority.P0 ? ChunkOrigin.Demand : ChunkOrigin.Prefetch);
            return data;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// For backends without ranged reads: downloads the file once from the beginning and stores it
    /// chunk by chunk, so later chunks are served from cache. This is an honest full download, not streaming.
    /// </summary>
    private async Task<byte[]> FetchSequentialAsync(string accountId, ICloudStorageProvider provider, CloudEntry entry, CachedFileRecord record, int index, int expected, FetchPriority priority)
    {
        var fetch = _sequential.GetOrAdd((accountId, record.FileKey), _ => new SequentialFetch());
        if (fetch.TryStart())
        {
            _ = Task.Run(() => RunSequentialAsync(accountId, provider, entry, record, fetch));
        }

        await fetch.WaitForChunkAsync(index).ConfigureAwait(false);
        return Store.TryRead(accountId, record.FileKey, index, expected)
               ?? throw new CloudProviderException(CloudErrorKind.Unknown, "Chunk could not be cached (cache full?).");
    }

    private async Task RunSequentialAsync(string accountId, ICloudStorageProvider provider, CloudEntry entry, CachedFileRecord record, SequentialFetch fetch)
    {
        var key = (accountId, record.FileKey);
        try
        {
            using var transfer = _transfers.Begin(accountId, entry.Path, TransferDirection.Download, entry.Size);
            await using var stream = await provider.OpenReadAsync(entry.Path, 0, null, CancellationToken.None).ConfigureAwait(false);
            var index = 0;
            var remaining = entry.Size;
            while (remaining > 0)
            {
                var size = (int)Math.Min(record.ChunkSize, remaining);
                var data = new byte[size];
                var n = await stream.ReadFullAsync(data, CancellationToken.None).ConfigureAwait(false);
                if (n != size)
                {
                    throw new CloudProviderException(CloudErrorKind.Network, "Download ended early.");
                }

                transfer.Add(n);
                if (!Store.Contains(accountId, record.FileKey, index))
                {
                    StoreChunk(accountId, record, index, data, ChunkOrigin.Demand, force: true);
                }

                fetch.Advance(index);
                index++;
                remaining -= size;
            }

            transfer.Complete();
            fetch.Complete(null);
        }
        catch (Exception ex)
        {
            fetch.Complete(ex);
        }
        finally
        {
            _sequential.TryRemove(key, out _);
        }
    }

    private async Task<int> ReadDirectAsync(string accountId, ICloudStorageProvider? provider, CloudEntry entry, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (provider == null)
        {
            throw new CloudProviderException(CloudErrorKind.Network, "Account is offline (file excluded from cache).");
        }

        using var transfer = _transfers.Begin(accountId, entry.Path, TransferDirection.Download, buffer.Length);
        await using var stream = await provider.OpenReadAsync(entry.Path, offset, buffer.Length, ct).ConfigureAwait(false);
        var n = await stream.ReadFullAsync(buffer, ct).ConfigureAwait(false);
        transfer.Add(n);
        transfer.Complete();
        _metrics.Increment(MetricNames.CacheMisses);
        return n;
    }

    private void StoreChunk(string accountId, CachedFileRecord record, int index, byte[] data, ChunkOrigin origin, bool force = false)
    {
        if (!EnsureSpace(data.Length) && !force)
        {
            Warning?.Invoke(this, "Cache budget or minimum free disk space reached; data is served without caching.");
            return;
        }

        try
        {
            Store.Write(accountId, record.FileKey, index, data, origin);
        }
        catch (IOException ex)
        {
            _log.Warn("cache", "Could not write chunk to cache.", ex, accountId);
        }
    }

    // ------------------------------------------------------------------ budget

    public long? DiskFreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(CacheDirectory);
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    private bool HasRoomFor(long bytes)
    {
        var s = _settings();
        var free = DiskFreeBytes();
        var fitsBudget = Store.TotalPhysicalBytes + bytes <= s.CacheBudgetBytes;
        var fitsDisk = free == null || free.Value - bytes >= s.MinFreeDiskBytes;
        return (fitsBudget && fitsDisk) || EnsureSpace(bytes);
    }

    /// <summary>Evicts LRU unpinned chunks until the new data fits the budget and the free-space floor.</summary>
    public bool EnsureSpace(long bytes)
    {
        var s = _settings();
        var needed = ChunkStore.DiskBytes(bytes);
        var overBudget = Store.TotalPhysicalBytes + needed - s.CacheBudgetBytes;
        var free = DiskFreeBytes();
        var underFloor = free == null ? 0 : s.MinFreeDiskBytes - (free.Value - needed);
        var toFree = Math.Max(overBudget, underFloor);
        if (toFree <= 0)
        {
            return true;
        }

        // Free a little extra (5% of budget) to avoid evicting on every single write.
        var target = toFree + Math.Min(s.CacheBudgetBytes / 20, 512L * 1024 * 1024);
        var freed = Store.EvictLru(target, IsPinned);
        return freed >= toFree;
    }

    public CacheStatus GetStatus(long stagingBytes)
    {
        var s = _settings();
        return new CacheStatus(s.CacheBudgetBytes, Store.TotalPhysicalBytes, s.MinFreeDiskBytes, DiskFreeBytes(), stagingBytes, CacheDirectory, Store.GetUsage());
    }

    // ------------------------------------------------------------------ pins & exclusions

    public bool IsPinned(string accountId, string path)
    {
        lock (_pinGate)
        {
            return _pins.TryGetValue(accountId, out var list) && list.Any(p => CloudPath.IsSameOrDescendant(p, path, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<(string AccountId, string Path)> GetPins()
    {
        lock (_pinGate)
        {
            return _pins.SelectMany(kv => kv.Value.Select(p => (kv.Key, p))).ToList();
        }
    }

    public void Pin(string accountId, string path)
    {
        _db.Execute("INSERT OR IGNORE INTO cache_pins(account_id, path, created_at) VALUES(@a,@p,@t)",
            ("@a", accountId), ("@p", CloudPath.Normalize(path)), ("@t", Clock.NowMs()));
        ReloadPins();
    }

    public void Unpin(string accountId, string path)
    {
        _db.Execute("DELETE FROM cache_pins WHERE account_id=@a AND path=@p", ("@a", accountId), ("@p", CloudPath.Normalize(path)));
        ReloadPins();
    }

    private void ReloadPins()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var (a, p) in _db.Query("SELECT account_id, path FROM cache_pins", r => (r.GetString(0), r.GetString(1))))
        {
            if (!map.TryGetValue(a, out var list))
            {
                map[a] = list = new List<string>();
            }

            list.Add(p);
        }

        lock (_pinGate)
        {
            _pins = map;
        }
    }

    public IReadOnlyList<(long Id, string? AccountId, string Pattern)> GetExclusions() =>
        _exclusions.Select(e => (e.Id, e.Account, e.Pattern)).ToList();

    /// <summary>Adds a glob exclusion ("*.tmp", "/Videos/**"). Excluded files are read directly, never cached or prefetched.</summary>
    public void AddExclusion(string? accountId, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Pattern is empty.");
        }

        GlobToRegex(pattern);
        _db.Execute("INSERT INTO cache_exclusions(account_id, pattern) VALUES(@a,@p)", ("@a", accountId), ("@p", pattern.Trim()));
        ReloadExclusions();
    }

    public void RemoveExclusion(long id)
    {
        _db.Execute("DELETE FROM cache_exclusions WHERE id=@id", ("@id", id));
        ReloadExclusions();
    }

    public bool IsExcluded(string accountId, string path)
    {
        var p = CloudPath.Normalize(path);
        var name = CloudPath.GetName(p);
        foreach (var (account, regex, pattern, _) in _exclusions)
        {
            if (account != null && account != accountId)
            {
                continue;
            }

            if (regex.IsMatch(pattern.Contains('/') ? p : name))
            {
                return true;
            }
        }

        return false;
    }

    private void ReloadExclusions()
    {
        _exclusions = _db.Query("SELECT id, account_id, pattern FROM cache_exclusions",
                r => (Account: AppDatabase.GetStringOrNull(r, 1), Pattern: r.GetString(2), Id: r.GetInt64(0)))
            .Select(x => (x.Account, GlobToRegex(x.Pattern), x.Pattern, x.Id))
            .ToList();
    }

    internal static Regex GlobToRegex(string glob)
    {
        var g = glob.Trim().Replace('\\', '/');
        var pattern = "^" + Regex.Escape(g)
            .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    // ------------------------------------------------------------------ maintenance

    public void ClearAccount(string accountId)
    {
        Store.ClearAccount(accountId);
        Metadata.ClearAccount(accountId);
        _db.Execute("DELETE FROM cache_pins WHERE account_id=@a", ("@a", accountId));
        _db.Execute("DELETE FROM cache_exclusions WHERE account_id=@a", ("@a", accountId));
        ReloadPins();
        ReloadExclusions();
    }

    /// <summary>Clears cached blocks only. Pending uploads (staging) are never touched by this.</summary>
    public void ClearAllBlocks()
    {
        foreach (var usage in Store.GetUsage())
        {
            Store.ClearAccount(usage.AccountId);
        }

        Metadata.InvalidateAll();
    }

    private void Maintenance()
    {
        try
        {
            Store.FlushAccessTimes();
            var s = _settings();
            if (Store.TotalPhysicalBytes > s.CacheBudgetBytes)
            {
                EnsureSpace(0);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("cache", "Cache maintenance failed.", ex);
        }
    }

    public void Dispose()
    {
        _maintenance.Dispose();
        try
        {
            Store.FlushAccessTimes();
        }
        catch (Exception)
        {
        }
    }

    private sealed class SequentialFetch
    {
        private readonly object _gate = new();
        private readonly List<(int Index, TaskCompletionSource Tcs)> _waiters = new();
        private int _available = -1;
        private Exception? _error;
        private bool _done;
        private int _started;

        public bool TryStart() => Interlocked.Exchange(ref _started, 1) == 0;

        public Task WaitForChunkAsync(int index)
        {
            lock (_gate)
            {
                if (index <= _available)
                {
                    return Task.CompletedTask;
                }

                if (_done)
                {
                    return _error != null ? Task.FromException(_error) : Task.CompletedTask;
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((index, tcs));
                return tcs.Task;
            }
        }

        public void Advance(int index)
        {
            List<TaskCompletionSource> ready;
            lock (_gate)
            {
                _available = index;
                ready = _waiters.Where(w => w.Index <= index).Select(w => w.Tcs).ToList();
                _waiters.RemoveAll(w => w.Index <= index);
            }

            ready.ForEach(t => t.TrySetResult());
        }

        public void Complete(Exception? error)
        {
            List<TaskCompletionSource> all;
            lock (_gate)
            {
                _done = true;
                _error = error;
                all = _waiters.Select(w => w.Tcs).ToList();
                _waiters.Clear();
            }

            foreach (var t in all)
            {
                if (error != null)
                {
                    t.TrySetException(error);
                }
                else
                {
                    t.TrySetResult();
                }
            }
        }
    }
}
