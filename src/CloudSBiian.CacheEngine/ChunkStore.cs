using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.Core.Storage;
using CloudSBiian.Monitoring;

namespace CloudSBiian.CacheEngine;

public enum ChunkOrigin
{
    Demand = 0,
    Prefetch = 1,
}

public sealed record CachedFileRecord(string AccountId, string Path, string FileKey, long Size, string Identity, int ChunkSize);

public sealed record AccountCacheUsage(string AccountId, long PhysicalBytes, long LogicalBytes, int Chunks, int Files);

/// <summary>
/// Block cache on NTFS. Every account has its own directory and every key is derived from
/// (account id, path), so one account can never address another account's chunks.
/// Chunks are written to a temp file and renamed (atomic), and verified with SHA-256.
/// </summary>
public sealed class ChunkStore
{
    private const int ClusterSize = 4096;
    private readonly AppDatabase _db;
    private readonly MetricsRegistry _metrics;
    private readonly IAppLog _log;
    private readonly ConcurrentDictionary<(string Account, string FileKey, int Index), long> _pendingAccess = new();
    private readonly object _usageGate = new();
    private readonly Dictionary<string, long> _usage = new();
    private readonly Func<bool> _verifyOnRead;

    public ChunkStore(string root, AppDatabase db, MetricsRegistry metrics, Func<bool>? verifyOnRead = null, IAppLog? log = null)
    {
        Root = Path.GetFullPath(root);
        _db = db;
        _metrics = metrics;
        _log = log ?? NullLog.Instance;
        _verifyOnRead = verifyOnRead ?? (() => true);
        Directory.CreateDirectory(Root);
        LoadUsage();
    }

    public string Root { get; }

    public static string FileKeyFor(string accountId, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\n" + CloudPath.Normalize(path)));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    public string AccountDirectory(string accountId)
    {
        Core.Security.AccountIdValidator.Validate(accountId);
        return Path.Combine(Root, accountId);
    }

    private string ChunkPath(string accountId, string fileKey, int index) =>
        Path.Combine(AccountDirectory(accountId), "chunks", fileKey[..2], $"{fileKey}_{index}.bin");

    public static long DiskBytes(long length) => (length + ClusterSize - 1) / ClusterSize * ClusterSize;

    public long TotalPhysicalBytes
    {
        get
        {
            lock (_usageGate)
            {
                return _usage.Values.Sum();
            }
        }
    }

    public long PhysicalBytes(string accountId)
    {
        lock (_usageGate)
        {
            return _usage.TryGetValue(accountId, out var v) ? v : 0;
        }
    }

    /// <summary>
    /// Ensures a file record matches the current remote identity. If the remote file changed,
    /// all chunks of the old version are discarded.
    /// </summary>
    public CachedFileRecord EnsureFile(string accountId, CloudEntry entry, int chunkSize)
    {
        var path = CloudPath.Normalize(entry.Path);
        var existing = GetFile(accountId, path);
        if (existing != null && existing.Identity == entry.Identity && existing.ChunkSize == chunkSize)
        {
            return existing;
        }

        var key = FileKeyFor(accountId, path);
        if (existing != null)
        {
            RemoveChunks(accountId, existing.FileKey);
        }

        _db.Execute(
            "INSERT OR REPLACE INTO cache_files(account_id, path, file_key, size, identity, chunk_size, last_access) VALUES(@a,@p,@k,@s,@i,@c,@t)",
            ("@a", accountId), ("@p", path), ("@k", key), ("@s", entry.Size), ("@i", entry.Identity), ("@c", chunkSize), ("@t", Clock.NowMs()));
        return new CachedFileRecord(accountId, path, key, entry.Size, entry.Identity, chunkSize);
    }

    public CachedFileRecord? GetFile(string accountId, string path) =>
        _db.Query(
            "SELECT path, file_key, size, identity, chunk_size FROM cache_files WHERE account_id=@a AND path=@p",
            r => new CachedFileRecord(accountId, r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), (int)r.GetInt64(4)),
            ("@a", accountId), ("@p", CloudPath.Normalize(path))).FirstOrDefault();

    /// <summary>Reads a cached chunk. Returns null on miss, missing file or checksum mismatch.</summary>
    public byte[]? TryRead(string accountId, string fileKey, int index, int expectedLength)
    {
        var row = _db.Query(
            "SELECT length, sha256, origin, used FROM cache_chunks WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
            r => (Length: r.GetInt64(0), Sha: r.GetString(1), Origin: (int)r.GetInt64(2), Used: r.GetInt64(3) != 0),
            ("@a", accountId), ("@k", fileKey), ("@i", index)).FirstOrDefault();
        if (row == default)
        {
            return null;
        }

        var file = ChunkPath(accountId, fileKey, index);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("cache", $"Chunk missing on disk, discarding record ({fileKey}#{index}).", null, accountId);
            RemoveChunk(accountId, fileKey, index);
            return null;
        }

        if (data.Length != row.Length || data.Length != expectedLength ||
            (_verifyOnRead() && !string.Equals(Hash(data), row.Sha, StringComparison.Ordinal)))
        {
            _metrics.Increment(MetricNames.CacheCorruptChunks);
            _log.Warn("cache", $"Corrupt chunk detected and discarded ({fileKey}#{index}).", null, accountId);
            RemoveChunk(accountId, fileKey, index);
            return null;
        }

        _pendingAccess[(accountId, fileKey, index)] = Clock.NowMs();
        if (row.Origin == (int)ChunkOrigin.Prefetch && !row.Used)
        {
            _metrics.Increment(MetricNames.PrefetchUseful);
            _db.Execute("UPDATE cache_chunks SET used=1 WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
                ("@a", accountId), ("@k", fileKey), ("@i", index));
        }

        return data;
    }

    public bool Contains(string accountId, string fileKey, int index) =>
        (_db.Scalar<long?>("SELECT 1 FROM cache_chunks WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
            ("@a", accountId), ("@k", fileKey), ("@i", index)) ?? 0) == 1;

    public void Write(string accountId, string fileKey, int index, ReadOnlySpan<byte> data, ChunkOrigin origin)
    {
        var file = ChunkPath(accountId, fileKey, index);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp-" + Environment.CurrentManagedThreadId + "-" + Guid.NewGuid().ToString("N")[..8];
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(data);
        }

        File.Move(tmp, file, overwrite: true);
        var disk = DiskBytes(data.Length);
        var length = data.Length;
        var hash = Hash(data);
        long previous = 0;
        _db.InTransaction((c, tx) =>
        {
            using (var q = AppDatabase.Build(c, "SELECT disk_bytes FROM cache_chunks WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
                       [("@a", accountId), ("@k", fileKey), ("@i", index)], tx))
            {
                previous = q.ExecuteScalar() is long p ? p : 0;
            }

            using var cmd = AppDatabase.Build(c,
                "INSERT OR REPLACE INTO cache_chunks(account_id, file_key, chunk_index, length, disk_bytes, sha256, last_access, origin, used) VALUES(@a,@k,@i,@l,@d,@h,@t,@o,@u)",
                [("@a", accountId), ("@k", fileKey), ("@i", index), ("@l", length), ("@d", disk), ("@h", hash), ("@t", Clock.NowMs()),
                 ("@o", (int)origin), ("@u", origin == ChunkOrigin.Demand ? 1 : 0)], tx);
            cmd.ExecuteNonQuery();
        });
        AddUsage(accountId, disk - previous);
    }

    /// <summary>Persists batched last-access timestamps (used by LRU eviction).</summary>
    public void FlushAccessTimes()
    {
        if (_pendingAccess.IsEmpty)
        {
            return;
        }

        var batch = _pendingAccess.ToArray();
        foreach (var kv in batch)
        {
            _pendingAccess.TryRemove(kv.Key, out _);
        }

        _db.InTransaction((c, tx) =>
        {
            foreach (var ((a, k, i), t) in batch)
            {
                using var cmd = AppDatabase.Build(c, "UPDATE cache_chunks SET last_access=@t WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
                    [("@t", t), ("@a", a), ("@k", k), ("@i", i)], tx);
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Evicts least-recently-used, unpinned chunks until <paramref name="bytesToFree"/> are released.</summary>
    public long EvictLru(long bytesToFree, Func<string, string, bool> isPinned)
    {
        FlushAccessTimes();
        long freed = 0;
        var offset = 0;
        while (freed < bytesToFree)
        {
            var candidates = _db.Query(
                "SELECT c.account_id, c.file_key, c.chunk_index, c.disk_bytes, f.path FROM cache_chunks c LEFT JOIN cache_files f ON f.account_id=c.account_id AND f.file_key=c.file_key ORDER BY c.last_access LIMIT 256 OFFSET @o",
                r => (Account: r.GetString(0), Key: r.GetString(1), Index: (int)r.GetInt64(2), Disk: r.GetInt64(3), Path: AppDatabase.GetStringOrNull(r, 4)),
                ("@o", offset));
            if (candidates.Count == 0)
            {
                break;
            }

            foreach (var c in candidates)
            {
                if (c.Path != null && isPinned(c.Account, c.Path))
                {
                    offset++;
                    continue;
                }

                RemoveChunk(c.Account, c.Key, c.Index);
                _metrics.Increment(MetricNames.CacheEvictions);
                freed += c.Disk;
                if (freed >= bytesToFree)
                {
                    break;
                }
            }
        }

        return freed;
    }

    public void RemoveChunk(string accountId, string fileKey, int index)
    {
        var disk = _db.Scalar<long?>("SELECT disk_bytes FROM cache_chunks WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
            ("@a", accountId), ("@k", fileKey), ("@i", index)) ?? 0;
        _db.Execute("DELETE FROM cache_chunks WHERE account_id=@a AND file_key=@k AND chunk_index=@i",
            ("@a", accountId), ("@k", fileKey), ("@i", index));
        TryDelete(ChunkPath(accountId, fileKey, index));
        _pendingAccess.TryRemove((accountId, fileKey, index), out _);
        AddUsage(accountId, -disk);
    }

    public void RemoveChunks(string accountId, string fileKey)
    {
        var indexes = _db.Query("SELECT chunk_index FROM cache_chunks WHERE account_id=@a AND file_key=@k",
            r => (int)r.GetInt64(0), ("@a", accountId), ("@k", fileKey));
        foreach (var i in indexes)
        {
            RemoveChunk(accountId, fileKey, i);
        }
    }

    /// <summary>Removes cached data for a path (file) or a whole subtree. Cloud data is untouched.</summary>
    public int RemovePath(string accountId, string path, bool recursive)
    {
        var p = CloudPath.Normalize(path);
        var files = _db.Query("SELECT path, file_key FROM cache_files WHERE account_id=@a",
                r => (Path: r.GetString(0), Key: r.GetString(1)), ("@a", accountId))
            .Where(f => recursive ? CloudPath.IsSameOrDescendant(p, f.Path, StringComparison.Ordinal) : f.Path == p)
            .ToList();
        foreach (var f in files)
        {
            RemoveChunks(accountId, f.Key);
            _db.Execute("DELETE FROM cache_files WHERE account_id=@a AND file_key=@k", ("@a", accountId), ("@k", f.Key));
        }

        return files.Count;
    }

    /// <summary>Deletes every chunk of one account (cloud data untouched).</summary>
    public void ClearAccount(string accountId)
    {
        _db.Execute("DELETE FROM cache_chunks WHERE account_id=@a", ("@a", accountId));
        _db.Execute("DELETE FROM cache_files WHERE account_id=@a", ("@a", accountId));
        var dir = Path.Combine(AccountDirectory(accountId), "chunks");
        if (Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                _log.Warn("cache", "Some cache files are in use and were not deleted.", ex, accountId);
            }
        }

        foreach (var key in _pendingAccess.Keys.Where(k => k.Account == accountId).ToList())
        {
            _pendingAccess.TryRemove(key, out _);
        }

        lock (_usageGate)
        {
            _usage.Remove(accountId);
        }
    }

    public IReadOnlyList<AccountCacheUsage> GetUsage() =>
        _db.Query(
            "SELECT c.account_id, SUM(c.disk_bytes), SUM(c.length), COUNT(*), COUNT(DISTINCT c.file_key) FROM cache_chunks c GROUP BY c.account_id",
            r => new AccountCacheUsage(r.GetString(0), r.GetInt64(1), r.GetInt64(2), (int)r.GetInt64(3), (int)r.GetInt64(4)));

    /// <summary>Lists the chunk indexes already cached for a file (used by Optimize &amp; Prepare).</summary>
    public HashSet<int> CachedChunks(string accountId, string fileKey) =>
        _db.Query("SELECT chunk_index FROM cache_chunks WHERE account_id=@a AND file_key=@k", r => (int)r.GetInt64(0),
            ("@a", accountId), ("@k", fileKey)).ToHashSet();

    /// <summary>
    /// Full integrity pass: drops DB rows whose files are missing or corrupt, and deletes orphan
    /// files / interrupted temp files. Returns (checked, removed).
    /// </summary>
    public (int Checked, int Removed) VerifyAll(CancellationToken ct)
    {
        var rows = _db.Query("SELECT account_id, file_key, chunk_index, length, sha256 FROM cache_chunks",
            r => (A: r.GetString(0), K: r.GetString(1), I: (int)r.GetInt64(2), L: r.GetInt64(3), H: r.GetString(4)));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var file = ChunkPath(row.A, row.K, row.I);
            known.Add(file);
            byte[]? data = null;
            try
            {
                data = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (data == null || data.Length != row.L || Hash(data) != row.H)
            {
                RemoveChunk(row.A, row.K, row.I);
                _metrics.Increment(MetricNames.CacheCorruptChunks);
                removed++;
            }
        }

        removed += RemoveOrphans(known);
        RecalculateUsage();
        return (rows.Count, removed);
    }

    /// <summary>Startup recovery: removes interrupted temp files left by a crash.</summary>
    public int CleanupTempFiles()
    {
        var count = 0;
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        foreach (var tmp in Directory.EnumerateFiles(Root, "*.tmp-*", SearchOption.AllDirectories))
        {
            if (TryDelete(tmp))
            {
                count++;
            }
        }

        return count;
    }

    private int RemoveOrphans(HashSet<string> known)
    {
        var count = 0;
        foreach (var accountDir in Directory.Exists(Root) ? Directory.GetDirectories(Root) : [])
        {
            var chunks = Path.Combine(accountDir, "chunks");
            if (!Directory.Exists(chunks))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(chunks, "*", SearchOption.AllDirectories))
            {
                if (!known.Contains(file) && TryDelete(file))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private void LoadUsage()
    {
        lock (_usageGate)
        {
            _usage.Clear();
            foreach (var (a, d) in _db.Query("SELECT account_id, SUM(disk_bytes) FROM cache_chunks GROUP BY account_id",
                         r => (r.GetString(0), r.GetInt64(1))))
            {
                _usage[a] = d;
            }
        }
    }

    public void RecalculateUsage() => LoadUsage();

    private void AddUsage(string accountId, long delta)
    {
        if (delta == 0)
        {
            return;
        }

        lock (_usageGate)
        {
            _usage[accountId] = Math.Max(0, (_usage.TryGetValue(accountId, out var v) ? v : 0) + delta);
        }
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    private static bool TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }
}
