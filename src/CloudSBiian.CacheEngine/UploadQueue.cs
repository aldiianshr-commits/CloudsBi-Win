using System.Collections.Concurrent;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.Core.Storage;
using CloudSBiian.Monitoring;

namespace CloudSBiian.CacheEngine;

public enum UploadState
{
    PendingUpload,
    Uploading,
    Synced,
    RetryRequired,
    Conflict,
    Offline,
}

public enum ConflictResolution
{
    /// <summary>Overwrite the cloud version with the local changes.</summary>
    KeepLocal,
    /// <summary>Upload the local changes under a new "(conflict ...)" name; keep the cloud file.</summary>
    KeepBoth,
    /// <summary>Throw away the local changes and use the cloud version.</summary>
    DiscardLocal,
}

public sealed record UploadItem(
    long Id,
    string AccountId,
    string Path,
    string StagingFile,
    long Size,
    UploadState State,
    int Attempts,
    string? LastError,
    string? BaseIdentity,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persistent write-back queue. Data stays in the staging area until the backend confirmed the upload;
/// nothing pending is ever evicted. Survives crashes: rows in "Uploading" go back to "PendingUpload" at start.
/// </summary>
public sealed class UploadQueue : IDisposable
{
    private readonly AppDatabase _db;
    private readonly StagingManager _staging;
    private readonly IProviderResolver _providers;
    private readonly TransferTracker _transfers;
    private readonly MetadataCache _metadata;
    private readonly ChunkStore _chunks;
    private readonly IAppLog _log;
    private readonly Func<int> _maxAttempts;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly ConcurrentQueue<UploadItem> _recentlySynced = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<UploadState>> _waiters = new();
    private Task? _worker;

    public UploadQueue(
        AppDatabase db,
        StagingManager staging,
        IProviderResolver providers,
        TransferTracker transfers,
        MetadataCache metadata,
        ChunkStore chunks,
        Func<int> maxAttempts,
        IAppLog? log = null)
    {
        _db = db;
        _staging = staging;
        _providers = providers;
        _transfers = transfers;
        _metadata = metadata;
        _chunks = chunks;
        _maxAttempts = maxAttempts;
        _log = log ?? NullLog.Instance;
        var recovered = _db.Execute("UPDATE upload_queue SET state=@p WHERE state=@u",
            ("@p", UploadState.PendingUpload.ToString()), ("@u", UploadState.Uploading.ToString()));
        if (recovered > 0)
        {
            _log.Info("uploads", $"Recovered {recovered} interrupted upload(s) after restart.");
        }
    }

    public int MaxParallelUploads { get; set; } = 2;

    public event EventHandler<UploadItem>? ItemChanged;

    public void Start()
    {
        _worker ??= Task.Run(() => WorkerLoopAsync(_stop.Token));
    }

    public IReadOnlyList<UploadItem> GetAll(string? accountId = null) =>
        _db.Query("SELECT id, account_id, path, staging_file, size, state, attempts, last_error, base_identity, next_attempt_at, created_at, updated_at FROM upload_queue WHERE (@a IS NULL OR account_id=@a) ORDER BY id",
            Map, ("@a", accountId));

    public IReadOnlyList<UploadItem> RecentlySynced => _recentlySynced.Reverse().ToList();

    public UploadItem? Get(string accountId, string path) =>
        _db.Query("SELECT id, account_id, path, staging_file, size, state, attempts, last_error, base_identity, next_attempt_at, created_at, updated_at FROM upload_queue WHERE account_id=@a AND path=@p",
            Map, ("@a", accountId), ("@p", CloudPath.Normalize(path))).FirstOrDefault();

    public UploadItem? Get(long id) =>
        _db.Query("SELECT id, account_id, path, staging_file, size, state, attempts, last_error, base_identity, next_attempt_at, created_at, updated_at FROM upload_queue WHERE id=@id",
            Map, ("@id", id)).FirstOrDefault();

    /// <summary>Pending (not yet synced) items directly inside a directory: shown in listings before they reach the cloud.</summary>
    public IReadOnlyList<UploadItem> PendingIn(string accountId, string directory)
    {
        var dir = CloudPath.Normalize(directory);
        return GetAll(accountId).Where(i => CloudPath.GetParent(i.Path) == dir).ToList();
    }

    public int CountPending() => (int)(_db.Scalar<long?>("SELECT COUNT(*) FROM upload_queue") ?? 0);

    public int CountByState(UploadState state) =>
        (int)(_db.Scalar<long?>("SELECT COUNT(*) FROM upload_queue WHERE state=@s", ("@s", state.ToString())) ?? 0);

    /// <summary>Queues (or re-queues) the staging file of a modified path.</summary>
    public UploadItem Enqueue(string accountId, string path, string stagingFile, long size, string? baseIdentity)
    {
        var p = CloudPath.Normalize(path);
        var now = Clock.NowMs();
        _db.Execute(
            """
            INSERT INTO upload_queue(account_id, path, staging_file, size, state, attempts, last_error, base_identity, next_attempt_at, created_at, updated_at)
            VALUES(@a,@p,@f,@s,@st,0,NULL,@b,0,@t,@t)
            ON CONFLICT(account_id, path) DO UPDATE SET staging_file=@f, size=@s, attempts=0, last_error=NULL, next_attempt_at=0, updated_at=@t,
                state=CASE WHEN state='Conflict' THEN 'Conflict' ELSE @st END
            """,
            ("@a", accountId), ("@p", p), ("@f", stagingFile), ("@s", size), ("@st", UploadState.PendingUpload.ToString()),
            ("@b", baseIdentity), ("@t", now));
        var item = Get(accountId, p)!;
        ItemChanged?.Invoke(this, item);
        _signal.Release();
        return item;
    }

    /// <summary>Waits until the item leaves the pending/uploading states (write-through-on-close mode).</summary>
    public async Task<UploadState> WaitForCompletionAsync(long id, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _waiters.GetOrAdd(id, _ => new TaskCompletionSource<UploadState>(TaskCreationOptions.RunContinuationsAsynchronously));
        _signal.Release();
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Get(id)?.State ?? UploadState.Synced;
        }
    }

    /// <summary>Renames a queued item (file renamed in Explorer before it reached the cloud).</summary>
    public void Rename(string accountId, string oldPath, string newPath, string newStagingFile)
    {
        _db.Execute("UPDATE upload_queue SET path=@n, staging_file=@f, updated_at=@t WHERE account_id=@a AND path=@o",
            ("@n", CloudPath.Normalize(newPath)), ("@f", newStagingFile), ("@t", Clock.NowMs()), ("@a", accountId), ("@o", CloudPath.Normalize(oldPath)));
    }

    /// <summary>Removes a queued item (the file was deleted locally) and its staging data.</summary>
    public void Cancel(string accountId, string path)
    {
        var item = Get(accountId, path);
        if (item == null)
        {
            return;
        }

        if (_running.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }

        _db.Execute("DELETE FROM upload_queue WHERE id=@id", ("@id", item.Id));
        _staging.Delete(accountId, item.Path);
        ItemChanged?.Invoke(this, item with { State = UploadState.Synced, LastError = "Cancelled" });
    }

    public void Retry(long id)
    {
        _db.Execute("UPDATE upload_queue SET state=@s, attempts=0, next_attempt_at=0, updated_at=@t WHERE id=@id AND state<>'Conflict'",
            ("@s", UploadState.PendingUpload.ToString()), ("@t", Clock.NowMs()), ("@id", id));
        Notify(id);
        _signal.Release();
    }

    public void RetryAll(string? accountId = null)
    {
        _db.Execute("UPDATE upload_queue SET state=@s, attempts=0, next_attempt_at=0 WHERE state IN ('RetryRequired','Offline') AND (@a IS NULL OR account_id=@a)",
            ("@s", UploadState.PendingUpload.ToString()), ("@a", accountId));
        _signal.Release();
    }

    /// <summary>Stops in-flight uploads of one account (e.g. before the account is removed). Data stays queued.</summary>
    public void StopAccount(string accountId)
    {
        foreach (var item in GetAll(accountId))
        {
            if (_running.TryGetValue(item.Id, out var cts))
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>Discards all pending data of an account. Only used after explicit user confirmation.</summary>
    public void DiscardAccount(string accountId)
    {
        StopAccount(accountId);
        _db.Execute("DELETE FROM upload_queue WHERE account_id=@a", ("@a", accountId));
        _staging.DeleteAccount(accountId);
    }

    public async Task ResolveConflictAsync(long id, ConflictResolution resolution, CancellationToken ct)
    {
        var item = Get(id) ?? throw new KeyNotFoundException();
        var provider = _providers.TryGetProvider(item.AccountId);
        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                if (provider == null)
                {
                    throw new CloudProviderException(CloudErrorKind.Network, "Account is not connected.");
                }

                var remote = await provider.StatAsync(item.Path, ct).ConfigureAwait(false);
                _db.Execute("UPDATE upload_queue SET state=@s, base_identity=@b, attempts=0, next_attempt_at=0 WHERE id=@id",
                    ("@s", UploadState.PendingUpload.ToString()), ("@b", remote?.Identity), ("@id", id));
                break;
            case ConflictResolution.KeepBoth:
                var name = CloudPath.GetName(item.Path);
                var ext = Path.GetExtension(name);
                var stem = Path.GetFileNameWithoutExtension(name);
                var newPath = CloudPath.Combine(CloudPath.GetParent(item.Path), $"{stem} (conflict {DateTime.Now:yyyy-MM-dd HHmmss}){ext}");
                var newLocal = _staging.Rename(item.AccountId, item.Path, newPath);
                _db.Execute("UPDATE upload_queue SET path=@p, staging_file=@f, state=@s, base_identity=NULL, attempts=0, next_attempt_at=0 WHERE id=@id",
                    ("@p", newPath), ("@f", newLocal), ("@s", UploadState.PendingUpload.ToString()), ("@id", id));
                break;
            case ConflictResolution.DiscardLocal:
                _db.Execute("DELETE FROM upload_queue WHERE id=@id", ("@id", id));
                _staging.Delete(item.AccountId, item.Path);
                _chunks.RemovePath(item.AccountId, item.Path, recursive: false);
                _metadata.Invalidate(item.AccountId, CloudPath.GetParent(item.Path));
                break;
        }

        Notify(id);
        _signal.Release();
    }

    private async Task WorkerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                var now = Clock.NowMs();
                var due = _db.Query(
                    "SELECT id, account_id, path, staging_file, size, state, attempts, last_error, base_identity, next_attempt_at, created_at, updated_at FROM upload_queue WHERE state IN ('PendingUpload','Offline') AND next_attempt_at<=@now ORDER BY id LIMIT 32",
                    Map, ("@now", now));
                var tasks = new List<Task>();
                foreach (var item in due)
                {
                    if (_running.Count >= MaxParallelUploads)
                    {
                        break;
                    }

                    if (_running.ContainsKey(item.Id))
                    {
                        continue;
                    }

                    // Files still open for writing are uploaded after the last handle closes.
                    var open = _staging.TryGetOpen(item.AccountId, item.Path);
                    if (open is { References: > 0 })
                    {
                        continue;
                    }

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    if (_running.TryAdd(item.Id, cts))
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessAsync(item, cts.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                _running.TryRemove(item.Id, out _);
                                cts.Dispose();
                                _signal.Release();
                            }
                        }, CancellationToken.None));
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("uploads", "Upload worker error.", ex);
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(UploadItem item, CancellationToken ct)
    {
        var provider = _providers.TryGetProvider(item.AccountId);
        if (provider == null)
        {
            SetState(item.Id, UploadState.Offline, "Account not connected.", item.Attempts, TimeSpan.FromSeconds(30));
            return;
        }

        if (!File.Exists(item.StagingFile))
        {
            SetState(item.Id, UploadState.RetryRequired, "Local data for this upload is missing.", item.Attempts, null);
            return;
        }

        var versionAtStart = item.UpdatedAt;
        SetState(item.Id, UploadState.Uploading, null, item.Attempts, null);
        using var transfer = _transfers.Begin(item.AccountId, item.Path, TransferDirection.Upload, new FileInfo(item.StagingFile).Length);
        try
        {
            var remote = await provider.StatAsync(item.Path, ct).ConfigureAwait(false);
            if (remote != null && remote.IsDirectory)
            {
                throw new CloudProviderException(CloudErrorKind.Conflict, "A folder with this name exists in the cloud.");
            }

            if (remote != null && remote.Identity != item.BaseIdentity)
            {
                // The cloud copy changed since we started editing (or a new file appeared): never overwrite silently.
                transfer.Fail("conflict");
                SetState(item.Id, UploadState.Conflict, "The file was changed in the cloud after it was opened here.", item.Attempts, null);
                _log.Warn("uploads", $"Conflict detected for {item.Path}.", null, item.AccountId);
                return;
            }

            if (provider is IFileUploadProvider fileUpload)
            {
                await fileUpload.UploadFileAsync(item.Path, item.StagingFile, transfer, ct).ConfigureAwait(false);
            }
            else
            {
                await using var fs = new FileStream(item.StagingFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
                await provider.UploadAsync(item.Path, fs, fs.Length, transfer, ct).ConfigureAwait(false);
            }

            var uploaded = await provider.StatAsync(item.Path, ct).ConfigureAwait(false);
            transfer.Complete();

            var current = Get(item.Id);
            if (current != null && current.UpdatedAt > versionAtStart)
            {
                // Modified again while uploading: upload the newer data next round, based on what we just wrote.
                _db.Execute("UPDATE upload_queue SET state=@s, base_identity=@b WHERE id=@id",
                    ("@s", UploadState.PendingUpload.ToString()), ("@b", uploaded?.Identity), ("@id", item.Id));
                Notify(item.Id);
                return;
            }

            _db.Execute("DELETE FROM upload_queue WHERE id=@id", ("@id", item.Id));
            _staging.Delete(item.AccountId, item.Path);
            _chunks.RemovePath(item.AccountId, item.Path, recursive: false);
            if (uploaded != null)
            {
                _metadata.Upsert(item.AccountId, uploaded);
            }

            _metadata.Invalidate(item.AccountId, CloudPath.GetParent(item.Path));
            var synced = item with { State = UploadState.Synced, UpdatedAt = DateTimeOffset.Now, LastError = null };
            _recentlySynced.Enqueue(synced);
            while (_recentlySynced.Count > 200 && _recentlySynced.TryDequeue(out _))
            {
            }

            _log.Info("uploads", $"Uploaded {item.Path}.", item.AccountId);
            ItemChanged?.Invoke(this, synced);
            Complete(item.Id, UploadState.Synced);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            transfer.Dispose();
            if (Get(item.Id) != null)
            {
                SetState(item.Id, UploadState.PendingUpload, "Upload interrupted.", item.Attempts, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            var kind = CloudProviderException.Classify(ex);
            transfer.Fail(ex.Message);
            _providers.ReportFailure(item.AccountId, ex);
            var attempts = item.Attempts + 1;
            var message = Core.Security.SecretRedactor.Redact(ex.Message);
            if (kind is CloudErrorKind.Network or CloudErrorKind.Timeout)
            {
                SetState(item.Id, UploadState.Offline, message, attempts, Backoff(attempts));
            }
            else if (kind == CloudErrorKind.Conflict)
            {
                SetState(item.Id, UploadState.Conflict, message, attempts, null);
            }
            else if (kind == CloudErrorKind.RateLimited && attempts < _maxAttempts())
            {
                SetState(item.Id, UploadState.PendingUpload, message, attempts, (ex as CloudProviderException)?.RetryAfter ?? Backoff(attempts));
            }
            else if (attempts >= _maxAttempts() || kind is CloudErrorKind.AuthenticationExpired or CloudErrorKind.AuthenticationFailed or CloudErrorKind.PermissionDenied or CloudErrorKind.InsufficientStorage)
            {
                SetState(item.Id, UploadState.RetryRequired, message, attempts, null);
            }
            else
            {
                SetState(item.Id, UploadState.PendingUpload, message, attempts, Backoff(attempts));
            }

            _log.Warn("uploads", $"Upload of {item.Path} failed (attempt {attempts}).", ex, item.AccountId);
        }
    }

    private static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));

    private void SetState(long id, UploadState state, string? error, int attempts, TimeSpan? retryIn)
    {
        _db.Execute("UPDATE upload_queue SET state=@s, last_error=@e, attempts=@n, next_attempt_at=@na WHERE id=@id",
            ("@s", state.ToString()), ("@e", error), ("@n", attempts),
            ("@na", retryIn.HasValue ? Clock.NowMs() + (long)retryIn.Value.TotalMilliseconds : 0), ("@id", id));
        Notify(id);
        if (state is UploadState.RetryRequired or UploadState.Conflict or UploadState.Offline)
        {
            Complete(id, state);
        }
    }

    private void Notify(long id)
    {
        var item = Get(id);
        if (item != null)
        {
            ItemChanged?.Invoke(this, item);
        }
    }

    private void Complete(long id, UploadState state)
    {
        if (_waiters.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(state);
        }
    }

    private static UploadItem Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetInt64(4),
        Enum.TryParse<UploadState>(r.GetString(5), out var s) ? s : UploadState.PendingUpload,
        (int)r.GetInt64(6),
        AppDatabase.GetStringOrNull(r, 7),
        AppDatabase.GetStringOrNull(r, 8),
        Clock.FromMs(r.GetInt64(9)),
        Clock.FromMs(r.GetInt64(10)),
        Clock.FromMs(r.GetInt64(11)));

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
