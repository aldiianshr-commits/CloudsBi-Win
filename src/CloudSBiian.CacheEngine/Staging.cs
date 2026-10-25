using System.Collections.Concurrent;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.CacheEngine;

/// <summary>
/// A local working copy of a cloud file opened for writing. Shared (ref-counted) by every handle
/// that has the same account/path open, so concurrent writers see the same data.
/// </summary>
public sealed class StagingFile : IDisposable
{
    private readonly StagingManager _owner;
    private readonly object _gate = new();
    private FileStream? _stream;
    private int _refs;

    internal StagingFile(StagingManager owner, string accountId, string cloudPath, string localPath)
    {
        _owner = owner;
        AccountId = accountId;
        CloudPath = cloudPath;
        LocalPath = localPath;
    }

    public string AccountId { get; }

    public string CloudPath { get; internal set; }

    public string LocalPath { get; internal set; }

    /// <summary>True once the remote content was fully copied in (or the file is new/truncated).</summary>
    public bool Hydrated { get; internal set; }

    /// <summary>Identity of the remote version this copy is based on (null for new files). Used for conflict detection.</summary>
    public string? BaseIdentity { get; internal set; }

    public bool Dirty { get; private set; }

    public int References => Volatile.Read(ref _refs);

    internal void AddRef() => Interlocked.Increment(ref _refs);

    private FileStream Stream
    {
        get
        {
            if (_stream == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
                _stream = new FileStream(LocalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            }

            return _stream;
        }
    }

    public long Length
    {
        get
        {
            lock (_gate)
            {
                return Stream.Length;
            }
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        lock (_gate)
        {
            var s = Stream;
            if (offset >= s.Length)
            {
                return 0;
            }

            s.Position = offset;
            var total = 0;
            while (total < buffer.Length)
            {
                var n = s.Read(buffer[total..]);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var s = Stream;
            s.Position = offset;
            s.Write(data);
            Dirty = true;
        }
    }

    public long Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var s = Stream;
            var offset = s.Length;
            s.Position = offset;
            s.Write(data);
            Dirty = true;
            return offset;
        }
    }

    public void SetLength(long length)
    {
        lock (_gate)
        {
            Stream.SetLength(length);
            Dirty = true;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _stream?.Flush(flushToDisk: true);
        }
    }

    public void MarkDirty() => Dirty = true;

    internal void ClearDirty() => Dirty = false;

    internal void CloseStream()
    {
        lock (_gate)
        {
            _stream?.Flush(flushToDisk: true);
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>Releases one reference. The underlying file handle is closed with the last one.</summary>
    public void Dispose() => _owner.Release(this);

    internal bool ReleaseRef() => Interlocked.Decrement(ref _refs) <= 0;
}

/// <summary>
/// Keeps write-back data in "{cache}\staging\{account}\". Staging files are never evicted and
/// survive restarts until the upload queue confirms they were uploaded.
/// </summary>
public sealed class StagingManager
{
    private readonly ConcurrentDictionary<(string Account, string Path), StagingFile> _open = new();
    private readonly object _gate = new();

    public StagingManager(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string LocalPathFor(string accountId, string cloudPath)
    {
        Core.Security.AccountIdValidator.Validate(accountId);
        var key = ChunkStore.FileKeyFor(accountId, cloudPath);
        return Path.Combine(Root, accountId, key + ".dat");
    }

    /// <summary>Opens (or joins) the staging file for a path. Caller must dispose.</summary>
    public StagingFile Acquire(string accountId, string cloudPath)
    {
        var p = Core.Paths.CloudPath.Normalize(cloudPath);
        lock (_gate)
        {
            var file = _open.GetOrAdd((accountId, p), k => new StagingFile(this, k.Account, k.Path, LocalPathFor(k.Account, k.Path)));
            file.AddRef();
            return file;
        }
    }

    public StagingFile? TryGetOpen(string accountId, string cloudPath) =>
        _open.TryGetValue((accountId, Core.Paths.CloudPath.Normalize(cloudPath)), out var f) ? f : null;

    public bool HasLocalCopy(string accountId, string cloudPath) => File.Exists(LocalPathFor(accountId, cloudPath));

    internal void Release(StagingFile file)
    {
        lock (_gate)
        {
            if (file.ReleaseRef())
            {
                file.CloseStream();
                _open.TryRemove((file.AccountId, file.CloudPath), out _);
            }
        }
    }

    /// <summary>
    /// Moves a staging entry to a new cloud path (rename before upload). The data file is renamed too,
    /// because its name is derived from the path. Returns the new local path.
    /// </summary>
    public string Rename(string accountId, string oldPath, string newPath)
    {
        lock (_gate)
        {
            var oldLocal = LocalPathFor(accountId, oldPath);
            var newLocal = LocalPathFor(accountId, newPath);
            _open.TryRemove((accountId, Core.Paths.CloudPath.Normalize(oldPath)), out var open);
            open?.CloseStream();
            if (File.Exists(oldLocal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newLocal)!);
                File.Move(oldLocal, newLocal, overwrite: true);
                var oldSidecar = Path.ChangeExtension(oldLocal, ".json");
                if (File.Exists(oldSidecar))
                {
                    File.Delete(oldSidecar);
                }

                var meta = new StagingSidecar(accountId, Core.Paths.CloudPath.Normalize(newPath), open?.BaseIdentity);
                File.WriteAllText(Path.ChangeExtension(newLocal, ".json"), System.Text.Json.JsonSerializer.Serialize(meta));
            }

            if (open != null)
            {
                // Same object, new key: existing handles keep working and reopen the moved file lazily.
                open.CloudPath = Core.Paths.CloudPath.Normalize(newPath);
                open.LocalPath = newLocal;
                _open[(accountId, open.CloudPath)] = open;
            }

            return newLocal;
        }
    }

    /// <summary>
    /// Writes "{key}.json" next to the data file so that, after a crash, data that was written but
    /// never queued can be recovered (we know which account/path it belongs to).
    /// </summary>
    public void WriteSidecar(StagingFile file)
    {
        var meta = new StagingSidecar(file.AccountId, file.CloudPath, file.BaseIdentity);
        var path = Path.ChangeExtension(file.LocalPath, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(meta));
    }

    /// <summary>Staging files whose sidecar names a path not in <paramref name="queued"/> (crash leftovers).</summary>
    public IReadOnlyList<(StagingSidecar Meta, string DataFile)> FindUnqueued(Func<string, string, bool> isQueued)
    {
        var result = new List<(StagingSidecar, string)>();
        if (!Directory.Exists(Root))
        {
            return result;
        }

        foreach (var json in Directory.EnumerateFiles(Root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var meta = System.Text.Json.JsonSerializer.Deserialize<StagingSidecar>(File.ReadAllText(json));
                var data = Path.ChangeExtension(json, ".dat");
                if (meta == null || !File.Exists(data))
                {
                    File.Delete(json);
                    continue;
                }

                if (!isQueued(meta.AccountId, meta.Path))
                {
                    result.Add((meta, data));
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    public void Delete(string accountId, string cloudPath)
    {
        var local = LocalPathFor(accountId, cloudPath);
        try
        {
            var sidecar = Path.ChangeExtension(local, ".json");
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            if (File.Exists(local))
            {
                File.Delete(local);
            }
        }
        catch (IOException)
        {
            // Still open by a handle; it will be cleaned up on the next start.
        }
    }

    public long TotalBytes()
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        return new DirectoryInfo(Root).EnumerateFiles("*.dat", SearchOption.AllDirectories).Sum(f => f.Length);
    }

    public long AccountBytes(string accountId)
    {
        var dir = Path.Combine(Root, accountId);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.dat").Sum(f => f.Length) : 0;
    }

    /// <summary>Deletes all staging data of an account (only after the user confirmed discarding pending uploads).</summary>
    public void DeleteAccount(string accountId)
    {
        var dir = Path.Combine(Root, accountId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed record StagingSidecar(string AccountId, string Path, string? BaseIdentity);
