using System.Buffers;
using System.Collections.Concurrent;
using CloudSBiian.CacheEngine;
using CloudSBiian.CloudProviders;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.PredictionEngine;

namespace CloudSBiian.VirtualDrive;

/// <summary>Services the drive needs. Shared by all mounted drives.</summary>
public sealed class DriveServices
{
    public required AccountManager Accounts { get; init; }

    public required CacheService Cache { get; init; }

    public required StagingManager Staging { get; init; }

    public required UploadQueue Uploads { get; init; }

    public PredictionService? Prediction { get; init; }

    public required Func<AppSettings> Settings { get; init; }

    public IAppLog Log { get; init; } = NullLog.Instance;
}

public readonly record struct NodeInfo(bool IsDirectory, long Size, DateTimeOffset Created, DateTimeOffset Modified, bool ReadOnly, bool Pending);

/// <summary>State of one open handle.</summary>
public sealed class DriveNode
{
    internal DriveNode(string? accountId, string cloudPath, bool isDirectory, bool isVirtualRoot, CloudEntry? entry)
    {
        AccountId = accountId;
        CloudPath = cloudPath;
        IsDirectory = isDirectory;
        IsVirtualRoot = isVirtualRoot;
        Entry = entry;
    }

    public string? AccountId { get; }

    public string CloudPath { get; internal set; }

    public bool IsDirectory { get; }

    public bool IsVirtualRoot { get; }

    /// <summary>Remote entry this handle was opened on (null for files created locally and not uploaded yet).</summary>
    public CloudEntry? Entry { get; internal set; }

    internal StagingFile? Staging { get; set; }

    internal bool Modified { get; set; }

    internal bool ExistsRemotely { get; set; }

    internal bool OpenRecorded { get; set; }

    internal ReadPattern Pattern { get; } = new();

    internal List<(string Name, NodeInfo Info)>? DirectorySnapshot { get; set; }

    internal object Lock { get; } = new();

    public bool IsAccountRoot => !IsVirtualRoot && CloudPath == Core.Paths.CloudPath.Root;
}

/// <summary>
/// File system semantics independent of WinFsp: path mapping (one folder per account or a single
/// account at the root), metadata, reads through the cache, write-back through staging and the
/// upload queue. <see cref="CloudFileSystem"/> adapts this to the WinFsp API.
/// </summary>
public sealed class DriveCore
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);
    private readonly DriveServices _s;
    private readonly DriveMountSettings _mount;
    private readonly ConcurrentDictionary<(string Account, string Path), UploadItem> _pending = new();
    private readonly object _mapGate = new();
    private Dictionary<string, string> _folderToAccount = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Folder, string AccountId)> _folders = new();
    private int _openHandles;

    public DriveCore(DriveServices services, DriveMountSettings mount)
    {
        _s = services;
        _mount = mount;
        RebuildAccountMap();
        foreach (var item in _s.Uploads.GetAll())
        {
            _pending[(item.AccountId, item.Path)] = item;
        }

        _s.Uploads.ItemChanged += OnUploadChanged;
        _s.Accounts.AccountsChanged += (_, _) => RebuildAccountMap();
        _s.Accounts.RuntimeChanged += (_, _) => RebuildAccountMap();
    }

    public int OpenHandles => Volatile.Read(ref _openHandles);

    public DriveMountSettings Mount => _mount;

    private AppSettings Settings => _s.Settings();

    public bool SingleAccount => _mount.AccountIds.Count == 1;

    public IReadOnlyList<(string Folder, string AccountId)> AccountFolders
    {
        get
        {
            lock (_mapGate)
            {
                return _folders.ToList();
            }
        }
    }

    // ------------------------------------------------------------------ path mapping

    internal void RebuildAccountMap()
    {
        var accounts = _s.Accounts.Runtimes
            .Where(r => r.Account.Enabled && (_mount.AccountIds.Count == 0 || _mount.AccountIds.Contains(r.Id)))
            .Select(r => r.Account)
            .ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (var a in accounts)
        {
            var baseName = SanitizeFolderName(a.DisplayName);
            var name = baseName;
            for (var i = 2; map.ContainsKey(name); i++)
            {
                name = $"{baseName} ({i})";
            }

            map[name] = a.Id;
            list.Add((name, a.Id));
        }

        lock (_mapGate)
        {
            _folderToAccount = map;
            _folders = list;
        }
    }

    internal static string SanitizeFolderName(string name)
    {
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "Account" : s;
    }

    /// <summary>
    /// Maps a Windows path to (account, cloud path). Windows is case-insensitive and WinFsp may
    /// hand us upper-cased components (e.g. on rename), while most backends are case-sensitive, so
    /// every existing component is replaced by its real spelling from the directory listings.
    /// </summary>
    internal (string? AccountId, string CloudPath, bool VirtualRoot) Resolve(string windowsPath, bool canonicalLeaf = true)
    {
        var p = CloudPath.Normalize(windowsPath);
        string account;
        string rest;
        if (SingleAccount)
        {
            account = _mount.AccountIds[0];
            rest = p;
        }
        else
        {
            if (p == CloudPath.Root)
            {
                return (null, CloudPath.Root, true);
            }

            var parts = p.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
            string? found;
            lock (_mapGate)
            {
                _folderToAccount.TryGetValue(parts[0], out found);
            }

            account = found ?? throw CloudProviderException.NotFound(windowsPath);
            rest = parts.Length > 1 ? "/" + parts[1] : CloudPath.Root;
        }

        return (account, Canonicalize(account, rest, canonicalLeaf), false);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DirectoryListing, NameLookup> NameIndex = new();

    private string Canonicalize(string accountId, string path, bool includeLeaf)
    {
        if (path == CloudPath.Root)
        {
            return path;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = CloudPath.Root;
        var provider = Provider(accountId);
        for (var i = 0; i < segments.Length; i++)
        {
            var name = segments[i];
            var isLeaf = i == segments.Length - 1;
            if (!isLeaf || includeLeaf)
            {
                name = RealName(accountId, provider, current, name) ?? name;
            }

            current = current == CloudPath.Root ? "/" + name : current + "/" + name;
        }

        return current;
    }

    private string? RealName(string accountId, ICloudStorageProvider? provider, string directory, string name)
    {
        foreach (var key in _pending.Keys)
        {
            if (key.Account == accountId && CloudPath.GetParent(key.Path) == directory &&
                string.Equals(CloudPath.GetName(key.Path), name, StringComparison.OrdinalIgnoreCase))
            {
                return CloudPath.GetName(key.Path);
            }
        }

        DirectoryListing listing;
        try
        {
            listing = Wait(_s.Cache.Metadata.ListAsync(accountId, provider, directory, CancellationToken.None));
        }
        catch (CloudProviderException)
        {
            return null;
        }

        var index = NameIndex.GetValue(listing, l => new NameLookup(l.Entries.Select(e => e.Name)));
        return index.Exact.Contains(name) ? name : index.IgnoreCase.TryGetValue(name, out var real) ? real : null;
    }

    private sealed class NameLookup
    {
        public NameLookup(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                Exact.Add(n);
                IgnoreCase.TryAdd(n, n);
            }
        }

        public HashSet<string> Exact { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> IgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Windows path ("\Folder\x") for an account path, used by the game launcher.</summary>
    public string? ToWindowsPath(string accountId, string cloudPath)
    {
        if (SingleAccount)
        {
            return _mount.AccountIds[0] == accountId ? CloudPath.ToWindows(cloudPath) : null;
        }

        string? folder;
        lock (_mapGate)
        {
            folder = _folders.FirstOrDefault(f => f.AccountId == accountId).Folder;
        }

        if (folder == null)
        {
            return null;
        }

        var rel = CloudPath.Normalize(cloudPath);
        return "\\" + folder + (rel == CloudPath.Root ? string.Empty : CloudPath.ToWindows(rel));
    }

    private ICloudStorageProvider? Provider(string accountId) => _s.Accounts.TryGetProvider(accountId);

    private ICloudStorageProvider RequireProvider(string accountId) =>
        Provider(accountId) ?? throw new CloudProviderException(CloudErrorKind.Network, "Account is not connected.");

    private bool IsReadOnly(string? accountId)
    {
        if (_mount.ReadOnly || accountId == null)
        {
            return true;
        }

        var runtime = _s.Accounts.Get(accountId);
        if (runtime?.Provider is { } p)
        {
            return (p.Capabilities & ProviderCapabilities.Upload) == 0;
        }

        return false;
    }

    private static T Wait<T>(Task<T> task) => task.WaitAsync(OperationTimeout).GetAwaiter().GetResult();

    private static void Wait(Task task) => task.WaitAsync(OperationTimeout).GetAwaiter().GetResult();

    // ------------------------------------------------------------------ metadata

    /// <summary>Returns info for a path or null when it does not exist.</summary>
    public NodeInfo? GetInfo(string windowsPath)
    {
        var (account, path, root) = Resolve(windowsPath);
        if (root)
        {
            return new NodeInfo(true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false);
        }

        return GetInfo(account!, path);
    }

    private NodeInfo? GetInfo(string accountId, string path)
    {
        if (_pending.TryGetValue((accountId, path), out var pending))
        {
            return PendingInfo(pending);
        }

        if (path == CloudPath.Root)
        {
            return new NodeInfo(true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false);
        }

        var entry = Wait(_s.Cache.Metadata.StatAsync(accountId, Provider(accountId), path, CancellationToken.None));
        return entry == null ? null : ToInfo(entry, IsReadOnly(accountId));
    }

    private NodeInfo PendingInfo(UploadItem item)
    {
        var open = _s.Staging.TryGetOpen(item.AccountId, item.Path);
        long size;
        DateTimeOffset modified;
        try
        {
            var fi = new FileInfo(item.StagingFile);
            size = open?.Length ?? (fi.Exists ? fi.Length : item.Size);
            modified = fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero) : item.UpdatedAt;
        }
        catch (IOException)
        {
            size = item.Size;
            modified = item.UpdatedAt;
        }

        return new NodeInfo(false, size, item.CreatedAt, modified, false, true);
    }

    private static NodeInfo ToInfo(CloudEntry e, bool readOnly) =>
        new(e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.Modified, e.Modified, readOnly && !e.IsDirectory, false);

    public NodeInfo GetInfo(DriveNode node)
    {
        if (node.IsVirtualRoot)
        {
            return new NodeInfo(true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, false);
        }

        lock (node.Lock)
        {
            if (node.Staging != null)
            {
                var now = DateTimeOffset.UtcNow;
                return new NodeInfo(false, node.Staging.Length, node.Entry?.Modified ?? now, node.Modified ? now : node.Entry?.Modified ?? now, false, true);
            }

            if (node.Entry != null)
            {
                return ToInfo(node.Entry, IsReadOnly(node.AccountId));
            }

            return new NodeInfo(node.IsDirectory, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, false);
        }
    }

    // ------------------------------------------------------------------ open / create

    public int Open(string windowsPath, out DriveNode? node)
    {
        node = null;
        var (account, path, root) = Resolve(windowsPath);
        if (root)
        {
            node = new DriveNode(null, CloudPath.Root, true, true, null);
            Interlocked.Increment(ref _openHandles);
            return NtStatus.Success;
        }

        if (_pending.TryGetValue((account!, path), out var pending))
        {
            node = new DriveNode(account, path, false, false, null) { ExistsRemotely = pending.BaseIdentity != null };
            node.Staging = _s.Staging.Acquire(account!, path);
            node.Staging.Hydrated = true;
            node.Staging.BaseIdentity = pending.BaseIdentity;
            Interlocked.Increment(ref _openHandles);
            return NtStatus.Success;
        }

        CloudEntry? entry = path == CloudPath.Root
            ? new CloudEntry(CloudPath.Root, string.Empty, true, 0, DateTimeOffset.UnixEpoch)
            : Wait(_s.Cache.Metadata.StatAsync(account!, Provider(account!), path, CancellationToken.None));
        if (entry == null)
        {
            return ParentExists(account!, path) ? NtStatus.ObjectNameNotFound : NtStatus.ObjectPathNotFound;
        }

        node = new DriveNode(account, path, entry.IsDirectory, false, entry) { ExistsRemotely = true };
        Interlocked.Increment(ref _openHandles);
        return NtStatus.Success;
    }

    private bool ParentExists(string accountId, string path)
    {
        var parent = CloudPath.GetParent(path);
        if (parent == CloudPath.Root)
        {
            return true;
        }

        try
        {
            return GetInfo(accountId, parent) is { IsDirectory: true };
        }
        catch (CloudProviderException)
        {
            return false;
        }
    }

    public int Create(string windowsPath, bool directory, out DriveNode? node)
    {
        node = null;
        var (account, path, root) = Resolve(windowsPath);
        if (root || path == CloudPath.Root)
        {
            return NtStatus.ObjectNameCollision;
        }

        if (IsReadOnly(account))
        {
            return NtStatus.MediaWriteProtected;
        }

        var name = CloudPath.GetName(path);
        if (!CloudPath.TryValidateName(name, out _))
        {
            return NtStatus.ObjectNameInvalid;
        }

        var parentInfo = GetInfo(account!, CloudPath.GetParent(path));
        if (parentInfo is not { IsDirectory: true })
        {
            return NtStatus.ObjectPathNotFound;
        }

        if (GetInfo(account!, path) != null)
        {
            return NtStatus.ObjectNameCollision;
        }

        var now = DateTimeOffset.UtcNow;
        if (directory)
        {
            var provider = RequireProvider(account!);
            Wait(provider.CreateDirectoryAsync(path, CancellationToken.None));
            var entry = new CloudEntry(path, name, true, 0, now);
            _s.Cache.Metadata.Upsert(account!, entry);
            node = new DriveNode(account, path, true, false, entry) { ExistsRemotely = true };
            Interlocked.Increment(ref _openHandles);
            _s.Log.Info("drive", $"Folder created: {path}", account);
            return NtStatus.Success;
        }

        var staging = _s.Staging.Acquire(account!, path);
        staging.SetLength(0);
        staging.Hydrated = true;
        staging.BaseIdentity = null;
        _s.Staging.WriteSidecar(staging);
        // Queue immediately so the new file is visible in listings; it is uploaded after the last handle closes.
        var item = _s.Uploads.Enqueue(account!, path, staging.LocalPath, 0, null);
        _pending[(account!, path)] = item;
        node = new DriveNode(account, path, false, false, null) { Staging = staging, Modified = true, ExistsRemotely = false };
        Interlocked.Increment(ref _openHandles);
        return NtStatus.Success;
    }

    public int Overwrite(DriveNode node)
    {
        if (node.IsDirectory)
        {
            return NtStatus.FileIsADirectory;
        }

        if (IsReadOnly(node.AccountId))
        {
            return NtStatus.MediaWriteProtected;
        }

        lock (node.Lock)
        {
            EnsureStaging(node, truncate: true);
            node.Staging!.SetLength(0);
            node.Modified = true;
        }

        return NtStatus.Success;
    }

    // ------------------------------------------------------------------ data

    public int Read(DriveNode node, long offset, Span<byte> destination, out int bytesRead)
    {
        bytesRead = 0;
        if (node.IsDirectory)
        {
            return NtStatus.FileIsADirectory;
        }

        StagingFile? staging;
        CloudEntry? entry;
        lock (node.Lock)
        {
            staging = node.Staging;
            entry = node.Entry;
        }

        if (staging != null)
        {
            if (offset >= staging.Length)
            {
                return NtStatus.EndOfFile;
            }

            bytesRead = staging.Read(offset, destination);
            return NtStatus.Success;
        }

        if (entry == null || offset >= entry.Size)
        {
            return NtStatus.EndOfFile;
        }

        if (!node.OpenRecorded)
        {
            node.OpenRecorded = true;
            Try(() => _s.Prediction?.OnFileOpened(node.AccountId!, entry));
        }

        var length = (int)Math.Min(destination.Length, entry.Size - offset);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var n = Wait(_s.Cache.ReadAsync(node.AccountId!, entry, offset, buffer.AsMemory(0, length), FetchPriority.P0, CancellationToken.None));
            buffer.AsSpan(0, n).CopyTo(destination);
            bytesRead = n;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var readCount = bytesRead;
        Try(() => _s.Prediction?.OnRead(node.AccountId!, entry, node.Pattern, offset, readCount));
        return readCount == 0 ? NtStatus.EndOfFile : NtStatus.Success;
    }

    public int Write(DriveNode node, long offset, ReadOnlySpan<byte> data, bool writeToEndOfFile, bool constrainedIo, out int written)
    {
        written = 0;
        if (node.IsDirectory)
        {
            return NtStatus.FileIsADirectory;
        }

        if (IsReadOnly(node.AccountId))
        {
            return NtStatus.MediaWriteProtected;
        }

        lock (node.Lock)
        {
            EnsureStaging(node, truncate: false);
            var staging = node.Staging!;
            var length = staging.Length;
            if (constrainedIo)
            {
                // Paging I/O must not extend the file.
                if (offset >= length)
                {
                    return NtStatus.Success;
                }

                data = data[..(int)Math.Min(data.Length, length - offset)];
            }

            if (writeToEndOfFile)
            {
                offset = length;
            }

            staging.Write(offset, data);
            written = data.Length;
            node.Modified = true;
        }

        return NtStatus.Success;
    }

    public int SetFileSize(DriveNode node, long newSize, bool allocationSizeOnly)
    {
        if (node.IsDirectory)
        {
            return NtStatus.FileIsADirectory;
        }

        if (IsReadOnly(node.AccountId))
        {
            return NtStatus.MediaWriteProtected;
        }

        lock (node.Lock)
        {
            var current = node.Staging?.Length ?? node.Entry?.Size ?? 0;
            if (allocationSizeOnly && newSize >= current)
            {
                // Allocation hints do not change content.
                return NtStatus.Success;
            }

            EnsureStaging(node, truncate: false);
            node.Staging!.SetLength(newSize);
            node.Modified = true;
        }

        return NtStatus.Success;
    }

    public int Flush(DriveNode node)
    {
        lock (node.Lock)
        {
            node.Staging?.Flush();
        }

        return NtStatus.Success;
    }

    /// <summary>
    /// Makes sure the handle has a full local working copy. Existing cloud files are downloaded
    /// (through the cache) before the first modification so partial writes stay correct.
    /// </summary>
    private void EnsureStaging(DriveNode node, bool truncate)
    {
        node.Staging ??= _s.Staging.Acquire(node.AccountId!, node.CloudPath);
        var staging = node.Staging;
        if (staging.Hydrated)
        {
            return;
        }

        if (_pending.ContainsKey((node.AccountId!, node.CloudPath)))
        {
            staging.Hydrated = true;
            return;
        }

        staging.SetLength(0);
        staging.BaseIdentity = node.Entry?.Identity;
        if (!truncate && node.Entry is { IsDirectory: false, Size: > 0 } entry)
        {
            var chunk = _s.Cache.ChunkSize;
            var buffer = ArrayPool<byte>.Shared.Rent(chunk);
            try
            {
                for (long offset = 0; offset < entry.Size;)
                {
                    var n = Wait(_s.Cache.ReadAsync(node.AccountId!, entry, offset, buffer.AsMemory(0, chunk), FetchPriority.P0, CancellationToken.None));
                    if (n == 0)
                    {
                        throw new CloudProviderException(CloudErrorKind.Network, "Could not download the file before editing it.");
                    }

                    staging.Write(offset, buffer.AsSpan(0, n));
                    offset += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        staging.Hydrated = true;
        _s.Staging.WriteSidecar(staging);
    }

    // ------------------------------------------------------------------ directories

    public IReadOnlyList<(string Name, NodeInfo Info)> ListDirectory(DriveNode node)
    {
        if (!node.IsDirectory)
        {
            throw new CloudProviderException(CloudErrorKind.InvalidPath, "Not a directory.");
        }

        if (node.IsVirtualRoot)
        {
            return AccountFolders
                .Select(f => (f.Folder, new NodeInfo(true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, false)))
                .OrderBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var account = node.AccountId!;
        var listing = Wait(_s.Cache.Metadata.ListAsync(account, Provider(account), node.CloudPath, CancellationToken.None));
        var readOnly = IsReadOnly(account);
        var result = new Dictionary<string, NodeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in listing.Entries)
        {
            if (!CloudPath.TryValidateName(e.Name, out _))
            {
                // Names Windows cannot represent (e.g. "a:b") are hidden rather than corrupted.
                continue;
            }

            result.TryAdd(e.Name, ToInfo(e, readOnly));
        }

        foreach (var (key, item) in _pending)
        {
            if (key.Account == account && CloudPath.GetParent(key.Path) == node.CloudPath)
            {
                result[CloudPath.GetName(key.Path)] = PendingInfo(item);
            }
        }

        Try(() => _s.Prediction?.OnDirectoryListed(account, node.CloudPath));
        return result.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ delete / rename

    public int CanDelete(DriveNode node)
    {
        if (node.IsVirtualRoot || node.IsAccountRoot)
        {
            return NtStatus.AccessDenied;
        }

        if (IsReadOnly(node.AccountId))
        {
            return NtStatus.MediaWriteProtected;
        }

        if (node.IsDirectory)
        {
            if (ListDirectory(node).Count > 0)
            {
                return NtStatus.DirectoryNotEmpty;
            }
        }

        if (node.ExistsRemotely && Provider(node.AccountId!) == null)
        {
            return NtStatus.UnexpectedNetworkError;
        }

        return NtStatus.Success;
    }

    /// <summary>Last handle closed (or delete-on-close). Finalizes deletes and queues modified files.</summary>
    public void Cleanup(DriveNode node, bool delete)
    {
        if (node.IsVirtualRoot)
        {
            return;
        }

        var account = node.AccountId!;
        try
        {
            if (delete)
            {
                DeleteNow(node);
                return;
            }

            UploadItem? queued = null;
            lock (node.Lock)
            {
                if (node.Modified && node.Staging != null)
                {
                    node.Staging.Flush();
                    var size = node.Staging.Length;
                    _s.Staging.WriteSidecar(node.Staging);
                    queued = _s.Uploads.Enqueue(account, node.CloudPath, node.Staging.LocalPath, size, node.Staging.BaseIdentity);
                    _pending[(account, node.CloudPath)] = queued;
                    node.Modified = false;
                    _s.Log.Info("drive", $"Queued upload: {node.CloudPath} ({size} bytes).", account);
                }
            }

            if (queued != null && Settings.WriteMode == WriteMode.WriteThroughOnClose)
            {
                // Release our reference first: the queue only uploads files without open handles.
                CloseStaging(node);
                var state = _s.Uploads.WaitForCompletionAsync(queued.Id, TimeSpan.FromMinutes(10), CancellationToken.None).GetAwaiter().GetResult();
                if (state != UploadState.Synced)
                {
                    _s.Log.Warn("drive", $"Write-through upload of {node.CloudPath} ended as {state}; data kept in the queue.", null, account);
                }
            }
        }
        catch (Exception ex)
        {
            _s.Log.Error("drive", $"Cleanup failed for {node.CloudPath}.", ex, account);
        }
    }

    private void DeleteNow(DriveNode node)
    {
        var account = node.AccountId!;
        var path = node.CloudPath;
        if (node.IsDirectory)
        {
            Wait(RequireProvider(account).DeleteDirectoryAsync(path, CancellationToken.None));
        }
        else
        {
            var wasPending = _pending.TryRemove((account, path), out _);
            CloseStaging(node);
            if (wasPending)
            {
                _s.Uploads.Cancel(account, path);
            }

            if (node.ExistsRemotely)
            {
                try
                {
                    Wait(RequireProvider(account).DeleteFileAsync(path, CancellationToken.None));
                }
                catch (CloudProviderException ex) when (ex.Kind == CloudErrorKind.NotFound)
                {
                }
            }

            _s.Staging.Delete(account, path);
        }

        _s.Cache.Store.RemovePath(account, path, recursive: node.IsDirectory);
        _s.Cache.Metadata.Remove(account, path);
        _s.Cache.Metadata.Invalidate(account, CloudPath.GetParent(path));
        _s.Log.Info("drive", $"Deleted {(node.IsDirectory ? "folder" : "file")}: {path}", account);
    }

    public int Rename(DriveNode node, string newWindowsPath, bool replaceIfExists)
    {
        if (node.IsVirtualRoot || node.IsAccountRoot)
        {
            return NtStatus.AccessDenied;
        }

        var (account, newPath, root) = Resolve(newWindowsPath, canonicalLeaf: false);
        if (root || account != node.AccountId)
        {
            return NtStatus.NotSameDevice;
        }

        if (IsReadOnly(account))
        {
            return NtStatus.MediaWriteProtected;
        }

        if (!CloudPath.TryValidateName(CloudPath.GetName(newPath), out _))
        {
            return NtStatus.ObjectNameInvalid;
        }

        var oldPath = node.CloudPath;
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return NtStatus.Success;
        }

        var target = GetInfo(account!, newPath);
        var caseOnly = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (target != null && !caseOnly)
        {
            if (!replaceIfExists || target.Value.IsDirectory)
            {
                return NtStatus.ObjectNameCollision;
            }
        }

        lock (node.Lock)
        {
            var pendingOnly = _pending.ContainsKey((account!, oldPath)) && !node.ExistsRemotely;
            if (!pendingOnly)
            {
                Wait(RequireProvider(account!).MoveAsync(oldPath, newPath, node.IsDirectory, replaceIfExists, CancellationToken.None));
            }

            if (_pending.TryRemove((account!, oldPath), out var item))
            {
                var newLocal = _s.Staging.Rename(account!, oldPath, newPath);
                _s.Uploads.Rename(account!, oldPath, newPath, newLocal);
                _pending[(account!, newPath)] = item with { Path = newPath, StagingFile = newLocal };
            }
            else if (node.Staging != null)
            {
                _s.Staging.Rename(account!, oldPath, newPath);
            }

            if (node.IsDirectory)
            {
                foreach (var key in _pending.Keys.Where(k => k.Account == account && CloudPath.IsSameOrDescendant(oldPath, k.Path)).ToList())
                {
                    // Pending files inside a renamed folder follow the folder.
                    if (_pending.TryRemove(key, out var child))
                    {
                        var moved = CloudPath.Rebase(key.Path, oldPath, newPath);
                        var local = _s.Staging.Rename(account!, key.Path, moved);
                        _s.Uploads.Rename(account!, key.Path, moved, local);
                        _pending[(account!, moved)] = child with { Path = moved, StagingFile = local };
                    }
                }
            }

            node.CloudPath = newPath;
            if (node.Entry != null)
            {
                node.Entry = node.Entry with { Path = newPath, Name = CloudPath.GetName(newPath) };
            }
        }

        _s.Cache.Store.RemovePath(account!, oldPath, recursive: node.IsDirectory);
        _s.Cache.Metadata.Remove(account!, oldPath);
        _s.Cache.Metadata.Invalidate(account!, CloudPath.GetParent(oldPath));
        _s.Cache.Metadata.Invalidate(account!, CloudPath.GetParent(newPath));
        Try(() => _s.Prediction?.Profiles.RenamePath(account!, oldPath, newPath));
        _s.Log.Info("drive", $"Renamed {oldPath} -> {newPath}", account);
        return NtStatus.Success;
    }

    public void Close(DriveNode node)
    {
        Interlocked.Decrement(ref _openHandles);
        if (node.IsVirtualRoot)
        {
            return;
        }

        if (node.Entry != null && node.OpenRecorded)
        {
            Try(() => _s.Prediction?.OnFileClosed(node.AccountId!, node.Entry, node.Pattern));
        }

        var account = node.AccountId!;
        var path = node.CloudPath;
        var hadStaging = node.Staging != null;
        CloseStaging(node);
        if (hadStaging && !_pending.ContainsKey((account, path)) && _s.Staging.TryGetOpen(account, path) == null)
        {
            // Hydrated but never modified: drop the working copy (the block cache still has the data).
            _s.Staging.Delete(account, path);
        }
    }

    private static void CloseStaging(DriveNode node)
    {
        StagingFile? staging;
        lock (node.Lock)
        {
            staging = node.Staging;
            node.Staging = null;
        }

        staging?.Dispose();
    }

    // ------------------------------------------------------------------ volume

    public (ulong Total, ulong Free) VolumeSize()
    {
        long total = 0;
        long free = 0;
        var known = false;
        foreach (var (_, accountId) in AccountFolders)
        {
            var quota = _s.Accounts.Get(accountId)?.Quota;
            if (quota?.Total is long t)
            {
                total += t;
                free += quota.Free ?? Math.Max(0, t - (quota.Used ?? 0));
                known = true;
            }
        }

        if (!known)
        {
            var reported = Settings.UnknownQuotaReportedBytes;
            return ((ulong)reported, (ulong)reported);
        }

        return ((ulong)total, (ulong)Math.Max(0, free));
    }

    private void OnUploadChanged(object? sender, UploadItem item)
    {
        if (item.State == UploadState.Synced)
        {
            _pending.TryRemove((item.AccountId, item.Path), out _);
        }
        else
        {
            _pending[(item.AccountId, item.Path)] = item;
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _s.Log.Debug("drive", $"Non-critical: {ex.Message}");
        }
    }

    public void Detach()
    {
        _s.Uploads.ItemChanged -= OnUploadChanged;
    }
}
