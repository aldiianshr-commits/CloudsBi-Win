using System.Diagnostics;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.CloudProviders.Local;

/// <summary>
/// Storage backed by a local directory or a UNC share (\\nas\share). Useful for NAS devices
/// that are already reachable from Windows and as a deterministic backend for tests.
/// </summary>
public sealed class LocalFolderProvider : ICloudStorageProvider, IFileUploadProvider
{
    private readonly string _root;

    public LocalFolderProvider(string accountId, string rootDirectory, ProviderKind kind = ProviderKind.LocalFolder)
    {
        AccountId = accountId;
        Kind = kind;
        _root = Path.GetFullPath(rootDirectory);
    }

    public string AccountId { get; }

    public ProviderKind Kind { get; }

    public ProviderCapabilities Capabilities => ProviderCapabilities.Full | ProviderCapabilities.Quota;

    private string Local(string cloudPath) => CloudPath.ToLocalPath(_root, cloudPath);

    public Task ConnectAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new CloudProviderException(CloudErrorKind.NotFound, $"Folder not reachable: {_root}");
        }

        return Task.CompletedTask;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!Directory.Exists(_root))
            {
                return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.NetworkError, $"Folder not reachable: {_root}", sw.Elapsed));
            }

            _ = Directory.EnumerateFileSystemEntries(_root).Take(1).ToList();
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.Ok, "Folder reachable.", sw.Elapsed, _root));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.AuthenticationFailed, ex.Message, sw.Elapsed));
        }
        catch (IOException ex)
        {
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.NetworkError, ex.Message, sw.Elapsed));
        }
    }

    public Task<IReadOnlyList<CloudEntry>> ListAsync(string path, CancellationToken ct) => Wrap(() =>
    {
        var dir = Local(path);
        if (!Directory.Exists(dir))
        {
            throw CloudProviderException.NotFound(path);
        }

        var basePath = CloudPath.Normalize(path);
        IReadOnlyList<CloudEntry> list = new DirectoryInfo(dir)
            .EnumerateFileSystemInfos()
            .Where(i => (i.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(i => ToEntry(basePath == CloudPath.Root ? "/" + i.Name : basePath + "/" + i.Name, i))
            .ToList();
        return list;
    });

    public Task<CloudEntry?> StatAsync(string path, CancellationToken ct) => Wrap(() =>
    {
        var p = CloudPath.Normalize(path);
        var local = Local(p);
        if (File.Exists(local))
        {
            return ToEntry(p, new FileInfo(local));
        }

        if (Directory.Exists(local))
        {
            return (CloudEntry?)ToEntry(p, new DirectoryInfo(local));
        }

        return null;
    });

    public Task<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => Wrap(() =>
    {
        var fs = new FileStream(Local(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        return (Stream)new ResponseStream(fs, new NoopDisposable(), length);
    });

    public async Task UploadAsync(string path, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var target = Local(path);
        var tmp = target + ".csb-upload";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await new ProgressStream(content, progress).CopyToAsync(fs, 1024 * 1024, ct).ConfigureAwait(false);
            }

            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw Map(ex, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public async Task UploadFileAsync(string path, string localFile, IProgress<long>? progress, CancellationToken ct)
    {
        await using var src = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await UploadAsync(path, src, src.Length, progress, ct).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Wrap(() =>
    {
        Directory.CreateDirectory(Local(path));
        return true;
    });

    public Task DeleteFileAsync(string path, CancellationToken ct) => Wrap(() =>
    {
        var local = Local(path);
        if (!File.Exists(local))
        {
            throw CloudProviderException.NotFound(path);
        }

        File.Delete(local);
        return true;
    });

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => Wrap(() =>
    {
        var local = Local(path);
        if (!Directory.Exists(local))
        {
            throw CloudProviderException.NotFound(path);
        }

        if (Directory.EnumerateFileSystemEntries(local).Any())
        {
            throw new CloudProviderException(CloudErrorKind.NotEmpty, $"Directory not empty: {path}");
        }

        Directory.Delete(local);
        return true;
    });

    public Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken ct) => Wrap(() =>
    {
        var src = Local(sourcePath);
        var dst = Local(destinationPath);
        if (isDirectory)
        {
            if (Directory.Exists(dst) || File.Exists(dst))
            {
                throw new CloudProviderException(CloudErrorKind.AlreadyExists, $"Destination exists: {destinationPath}");
            }

            Directory.Move(src, dst);
        }
        else
        {
            if (!overwrite && File.Exists(dst))
            {
                throw new CloudProviderException(CloudErrorKind.AlreadyExists, $"Destination exists: {destinationPath}");
            }

            File.Move(src, dst, overwrite);
        }

        return true;
    });

    public Task<QuotaInfo?> GetQuotaAsync(CancellationToken ct) => Wrap(() =>
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_root)!);
            return (QuotaInfo?)new QuotaInfo(drive.TotalSize, drive.TotalSize - drive.AvailableFreeSpace, drive.AvailableFreeSpace);
        }
        catch (ArgumentException)
        {
            // UNC roots are not supported by DriveInfo.
            return null;
        }
    });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CloudEntry ToEntry(string path, FileSystemInfo info) => info switch
    {
        FileInfo f => new CloudEntry(path, f.Name, false, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)),
        _ => new CloudEntry(path, info.Name, true, 0, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)),
    };

    private static Task<T> Wrap<T>(Func<T> work)
    {
        try
        {
            return Task.FromResult(work());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromException<T>(Map(ex, null));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static CloudProviderException Map(Exception ex, string? path) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => CloudProviderException.NotFound(path ?? ex.Message),
        UnauthorizedAccessException => new CloudProviderException(CloudErrorKind.PermissionDenied, ex.Message, ex),
        IOException io when io.HResult == unchecked((int)0x80070070) => new CloudProviderException(CloudErrorKind.InsufficientStorage, ex.Message, ex),
        IOException io when io.HResult == unchecked((int)0x80070050) || io.HResult == unchecked((int)0x800700B7) => new CloudProviderException(CloudErrorKind.AlreadyExists, ex.Message, ex),
        _ => new CloudProviderException(CloudErrorKind.Network, ex.Message, ex),
    };

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
