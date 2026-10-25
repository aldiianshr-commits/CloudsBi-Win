using CloudSBiian.Core.Storage;

namespace CloudSBiian.PredictionEngine;

public sealed record FileAccessStats(string AccountId, string Path, long Size, long OpenCount, long ReadBytes, long SequentialReads, long RandomReads, DateTimeOffset LastOpen)
{
    public double SequentialRatio => SequentialReads + RandomReads == 0 ? 0 : (double)SequentialReads / (SequentialReads + RandomReads);
}

public sealed record CoAccess(string PathA, string PathB, long Count, DateTimeOffset LastSeen);

public sealed record TraceChunk(string AccountId, string Path, int ChunkIndex, long FirstMs);

/// <summary>SQLite persistence of access profiles (no file content, only paths and counters).</summary>
public sealed class AccessProfileStore
{
    private readonly AppDatabase _db;

    public AccessProfileStore(AppDatabase db) => _db = db;

    public void RecordOpen(string accountId, string path, long size)
    {
        _db.Execute(
            """
            INSERT INTO pred_file_stats(account_id, path, size, open_count, last_open) VALUES(@a,@p,@s,1,@t)
            ON CONFLICT(account_id, path) DO UPDATE SET open_count=open_count+1, size=@s, last_open=@t
            """,
            ("@a", accountId), ("@p", path), ("@s", size), ("@t", Clock.NowMs()));
    }

    public void RecordReads(string accountId, string path, long bytes, int sequential, int random)
    {
        _db.Execute(
            "UPDATE pred_file_stats SET read_bytes=read_bytes+@b, sequential_reads=sequential_reads+@s, random_reads=random_reads+@r WHERE account_id=@a AND path=@p",
            ("@b", bytes), ("@s", sequential), ("@r", random), ("@a", accountId), ("@p", path));
    }

    public void RecordCoAccess(string accountId, string a, string b)
    {
        if (a == b)
        {
            return;
        }

        _db.Execute(
            """
            INSERT INTO pred_co_access(account_id, path_a, path_b, count, last_seen) VALUES(@acc,@a,@b,1,@t)
            ON CONFLICT(account_id, path_a, path_b) DO UPDATE SET count=count+1, last_seen=@t
            """,
            ("@acc", accountId), ("@a", a), ("@b", b), ("@t", Clock.NowMs()));
    }

    public void RecordFolderVisit(string accountId, string path)
    {
        _db.Execute(
            """
            INSERT INTO pred_folder_stats(account_id, path, visit_count, last_visit) VALUES(@a,@p,1,@t)
            ON CONFLICT(account_id, path) DO UPDATE SET visit_count=visit_count+1, last_visit=@t
            """,
            ("@a", accountId), ("@p", path), ("@t", Clock.NowMs()));
    }

    public FileAccessStats? GetStats(string accountId, string path) =>
        _db.Query("SELECT account_id, path, size, open_count, read_bytes, sequential_reads, random_reads, last_open FROM pred_file_stats WHERE account_id=@a AND path=@p",
            MapStats, ("@a", accountId), ("@p", path)).FirstOrDefault();

    public IReadOnlyList<FileAccessStats> TopFiles(int limit, string? accountId = null) =>
        _db.Query("SELECT account_id, path, size, open_count, read_bytes, sequential_reads, random_reads, last_open FROM pred_file_stats WHERE (@a IS NULL OR account_id=@a) ORDER BY open_count DESC, last_open DESC LIMIT @l",
            MapStats, ("@a", accountId), ("@l", limit));

    /// <summary>Files frequently opened after <paramref name="path"/>, with the conditional probability P(b | a).</summary>
    public IReadOnlyList<(string Path, double Probability, long Count)> Followers(string accountId, string path, int limit)
    {
        var opens = _db.Scalar<long?>("SELECT open_count FROM pred_file_stats WHERE account_id=@a AND path=@p", ("@a", accountId), ("@p", path)) ?? 0;
        if (opens == 0)
        {
            return Array.Empty<(string, double, long)>();
        }

        return _db.Query("SELECT path_b, count FROM pred_co_access WHERE account_id=@a AND path_a=@p ORDER BY count DESC LIMIT @l",
                r => (Path: r.GetString(0), Count: r.GetInt64(1)), ("@a", accountId), ("@p", path), ("@l", limit))
            .Select(x => (x.Path, Math.Min(1.0, (double)x.Count / opens), x.Count))
            .ToList();
    }

    public void AddTraceChunk(string traceId, string accountId, string path, int chunkIndex, long elapsedMs)
    {
        _db.Execute("INSERT OR IGNORE INTO pred_trace(trace_id, account_id, path, chunk_index, first_ms) VALUES(@id,@a,@p,@c,@ms)",
            ("@id", traceId), ("@a", accountId), ("@p", path), ("@c", chunkIndex), ("@ms", elapsedMs));
    }

    /// <summary>Recorded chunks of a launch trace in first-access order.</summary>
    public IReadOnlyList<TraceChunk> GetTrace(string traceId) =>
        _db.Query("SELECT account_id, path, chunk_index, first_ms FROM pred_trace WHERE trace_id=@id ORDER BY first_ms, path, chunk_index",
            r => new TraceChunk(r.GetString(0), r.GetString(1), (int)r.GetInt64(2), r.GetInt64(3)), ("@id", traceId));

    public void DeleteTrace(string traceId) => _db.Execute("DELETE FROM pred_trace WHERE trace_id=@id", ("@id", traceId));

    public (long Files, long Pairs, long TraceChunks) Counts() => (
        _db.Scalar<long?>("SELECT COUNT(*) FROM pred_file_stats") ?? 0,
        _db.Scalar<long?>("SELECT COUNT(*) FROM pred_co_access") ?? 0,
        _db.Scalar<long?>("SELECT COUNT(*) FROM pred_trace") ?? 0);

    public void Clear(string? accountId = null)
    {
        foreach (var table in new[] { "pred_file_stats", "pred_co_access", "pred_folder_stats", "pred_trace" })
        {
            _db.Execute($"DELETE FROM {table} WHERE (@a IS NULL OR account_id=@a)", ("@a", accountId));
        }
    }

    public void RenamePath(string accountId, string oldPath, string newPath)
    {
        _db.Execute("UPDATE OR IGNORE pred_file_stats SET path=@n WHERE account_id=@a AND path=@o", ("@n", newPath), ("@a", accountId), ("@o", oldPath));
        _db.Execute("UPDATE OR IGNORE pred_co_access SET path_a=@n WHERE account_id=@a AND path_a=@o", ("@n", newPath), ("@a", accountId), ("@o", oldPath));
        _db.Execute("UPDATE OR IGNORE pred_co_access SET path_b=@n WHERE account_id=@a AND path_b=@o", ("@n", newPath), ("@a", accountId), ("@o", oldPath));
    }

    private static FileAccessStats MapStats(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), Clock.FromMs(r.GetInt64(7)));
}
