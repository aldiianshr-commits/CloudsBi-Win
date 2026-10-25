using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;
using CloudSBiian.Core.Storage;
using CloudSBiian.Monitoring;

namespace CloudSBiian.CacheEngine;

public sealed record DirectoryListing(IReadOnlyList<CloudEntry> Entries, DateTimeOffset FetchedAt, bool IsStale);

/// <summary>
/// Directory/metadata cache. Listings are kept in memory with a TTL and persisted to SQLite so
/// folders can still be browsed while offline (flagged as stale). Isolated per account.
/// </summary>
public sealed class MetadataCache
{
    private readonly ConcurrentDictionary<(string Account, string Path), DirectoryListing> _memory = new();
    private readonly ConcurrentDictionary<(string Account, string Path), Lazy<Task<DirectoryListing>>> _inflight = new();
    private readonly AppDatabase _db;
    private readonly MetricsRegistry _metrics;
    private readonly Func<TimeSpan> _ttl;

    public MetadataCache(AppDatabase db, MetricsRegistry metrics, Func<TimeSpan> ttl)
    {
        _db = db;
        _metrics = metrics;
        _ttl = ttl;
    }

    public event EventHandler<(string AccountId, string Path)>? DirectoryRefreshed;

    public async Task<DirectoryListing> ListAsync(string accountId, ICloudStorageProvider? provider, string path, CancellationToken ct, bool forceRefresh = false)
    {
        var p = CloudPath.Normalize(path);
        var key = (accountId, p);
        _memory.TryGetValue(key, out var cached);
        if (!forceRefresh && cached != null && !cached.IsStale &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _ttl())
        {
            return cached;
        }

        if (provider == null)
        {
            return cached ?? LoadPersisted(accountId, p) ?? throw new CloudProviderException(CloudErrorKind.Network, "Account is not connected and no cached listing exists.");
        }

        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<DirectoryListing>>(() => FetchAsync(accountId, provider, p)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (CloudProviderException ex) when (ex.IsTransient)
        {
            // Offline fallback: last known listing, clearly marked as stale.
            var fallback = cached ?? LoadPersisted(accountId, p);
            if (fallback != null)
            {
                return fallback with { IsStale = true };
            }

            throw;
        }
    }

    private async Task<DirectoryListing> FetchAsync(string accountId, ICloudStorageProvider provider, string path)
    {
        var key = (accountId, path);
        try
        {
            var sw = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            IReadOnlyList<CloudEntry> entries;
            try
            {
                entries = await provider.ListAsync(path, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new CloudProviderException(CloudErrorKind.Timeout, $"Listing {path} timed out.");
            }

            _metrics.RecordLatency(MetricNames.ListLatency, sw.Elapsed);
            var listing = new DirectoryListing(entries, DateTimeOffset.UtcNow, false);
            _memory[key] = listing;
            Persist(accountId, path, listing);
            DirectoryRefreshed?.Invoke(this, (accountId, path));
            return listing;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>Looks up a single entry through its parent's listing (one list call instead of many stats).</summary>
    public async Task<CloudEntry?> StatAsync(string accountId, ICloudStorageProvider? provider, string path, CancellationToken ct)
    {
        var p = CloudPath.Normalize(path);
        if (p == CloudPath.Root)
        {
            return new CloudEntry(CloudPath.Root, string.Empty, true, 0, DateTimeOffset.UnixEpoch);
        }

        DirectoryListing parent;
        try
        {
            parent = await ListAsync(accountId, provider, CloudPath.GetParent(p), ct).ConfigureAwait(false);
        }
        catch (CloudProviderException ex) when (ex.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }

        var name = CloudPath.GetName(p);
        return parent.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal)) ??
               parent.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Inserts or replaces an entry in its parent listing after a local change.</summary>
    public void Upsert(string accountId, CloudEntry entry)
    {
        var parent = CloudPath.GetParent(entry.Path);
        var key = (accountId, parent);
        if (_memory.TryGetValue(key, out var listing))
        {
            var list = listing.Entries.Where(e => !string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(entry);
            _memory[key] = listing with { Entries = list };
        }
    }

    public void Remove(string accountId, string path)
    {
        var p = CloudPath.Normalize(path);
        var key = (accountId, CloudPath.GetParent(p));
        if (_memory.TryGetValue(key, out var listing))
        {
            _memory[key] = listing with { Entries = listing.Entries.Where(e => !string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase)).ToList() };
        }

        InvalidateTree(accountId, p);
    }

    /// <summary>Forces the next listing of <paramref name="directory"/> to hit the backend.</summary>
    public void Invalidate(string accountId, string directory) => _memory.TryRemove((accountId, CloudPath.Normalize(directory)), out _);

    public void InvalidateTree(string accountId, string path)
    {
        var p = CloudPath.Normalize(path);
        foreach (var key in _memory.Keys.Where(k => k.Account == accountId && CloudPath.IsSameOrDescendant(p, k.Path)).ToList())
        {
            _memory.TryRemove(key, out _);
        }
    }

    public void InvalidateAll(string? accountId = null)
    {
        foreach (var key in _memory.Keys.Where(k => accountId == null || k.Account == accountId).ToList())
        {
            _memory.TryRemove(key, out _);
        }
    }

    public void ClearAccount(string accountId)
    {
        InvalidateAll(accountId);
        _db.Execute("DELETE FROM dir_cache WHERE account_id=@a", ("@a", accountId));
    }

    private void Persist(string accountId, string path, DirectoryListing listing)
    {
        try
        {
            _db.Execute("INSERT OR REPLACE INTO dir_cache(account_id, path, entries_json, fetched_at) VALUES(@a,@p,@j,@t)",
                ("@a", accountId), ("@p", path), ("@j", JsonSerializer.Serialize(listing.Entries)), ("@t", listing.FetchedAt.ToUnixTimeMilliseconds()));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Persistence is an optimization for offline browsing only.
        }
    }

    private DirectoryListing? LoadPersisted(string accountId, string path)
    {
        var row = _db.Query("SELECT entries_json, fetched_at FROM dir_cache WHERE account_id=@a AND path=@p",
            r => (Json: r.GetString(0), At: r.GetInt64(1)), ("@a", accountId), ("@p", path)).FirstOrDefault();
        if (row.Json == null)
        {
            return null;
        }

        var entries = JsonSerializer.Deserialize<List<CloudEntry>>(row.Json) ?? new List<CloudEntry>();
        return new DirectoryListing(entries, Clock.FromMs(row.At), true);
    }
}
