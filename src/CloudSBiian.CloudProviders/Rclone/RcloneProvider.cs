using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.CloudProviders.Rclone;

/// <summary>
/// Storage adapter over a remote in the private rclone daemon. Reads use HTTP Range against
/// --rc-serve; whether the backend really honours ranges is verified at connect time.
/// </summary>
public sealed class RcloneProvider : ICloudStorageProvider, IFileUploadProvider
{
    private readonly RcloneDaemon _daemon;
    private readonly string _remote;
    private readonly string _root;
    private ProviderCapabilities _capabilities = ProviderCapabilities.List | ProviderCapabilities.Read | ProviderCapabilities.Upload |
                                                 ProviderCapabilities.Delete | ProviderCapabilities.CreateDirectory;

    public RcloneProvider(string accountId, ProviderKind kind, RcloneDaemon daemon, string rootPath)
    {
        AccountId = accountId;
        Kind = kind;
        _daemon = daemon;
        _remote = RcloneRemoteConfigurator.RemoteNameFor(accountId);
        _root = CloudPath.Normalize(rootPath).TrimStart('/');
    }

    public string AccountId { get; }

    public ProviderKind Kind { get; }

    public ProviderCapabilities Capabilities => _capabilities;

    /// <summary>"remote:root" string understood by every rc call.</summary>
    internal string Fs => $"{_remote}:{_root}";

    private static string Rel(string path) => CloudPath.Normalize(path).TrimStart('/');

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _daemon.EnsureStartedAsync(ct).ConfigureAwait(false);
        var info = await _daemon.CallAsync("operations/fsinfo", new { fs = Fs }, ct).ConfigureAwait(false);
        var caps = ProviderCapabilities.List | ProviderCapabilities.Read | ProviderCapabilities.Upload |
                   ProviderCapabilities.Delete | ProviderCapabilities.CreateDirectory;
        if (info["Features"] is JsonObject features)
        {
            if (IsTrue(features, "Move"))
            {
                caps |= ProviderCapabilities.Move | ProviderCapabilities.Rename;
            }

            if (IsTrue(features, "DirMove"))
            {
                caps |= ProviderCapabilities.DirectoryMove;
            }

            if (IsTrue(features, "About"))
            {
                caps |= ProviderCapabilities.Quota;
            }
        }
        else
        {
            caps |= ProviderCapabilities.Move | ProviderCapabilities.Rename;
        }

        // Without server-side Move rclone falls back to copy+delete, which still works for files.
        caps |= ProviderCapabilities.Move | ProviderCapabilities.Rename;
        _capabilities = caps | ProviderCapabilities.RangeRead;
    }

    /// <summary>
    /// Verifies that ranged reads really return only the requested bytes. Called by the account
    /// manager with a known file; clears <see cref="ProviderCapabilities.RangeRead"/> otherwise.
    /// </summary>
    public async Task<bool> ProbeRangeSupportAsync(CloudEntry file, CancellationToken ct)
    {
        if (file.IsDirectory || file.Size < 4)
        {
            return (_capabilities & ProviderCapabilities.RangeRead) != 0;
        }

        using var response = await _daemon.GetObjectAsync(Fs, Rel(file.Path), 1, 2, ct).ConfigureAwait(false);
        var ok = (int)response.StatusCode == 206;
        if (!ok)
        {
            _capabilities &= ~ProviderCapabilities.RangeRead;
        }

        return ok;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _daemon.EnsureStartedAsync(ct).ConfigureAwait(false);
            await _daemon.CallAsync("operations/list", new { fs = Fs, remote = string.Empty, opt = new { noModTime = true, noMimeType = true } }, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            return new ConnectionTestResult(ConnectionTestStatus.Ok, "Connected.", sw.Elapsed);
        }
        catch (CloudProviderException ex)
        {
            var status = ex.Kind switch
            {
                CloudErrorKind.AuthenticationExpired => ConnectionTestStatus.AuthenticationExpired,
                CloudErrorKind.AuthenticationFailed or CloudErrorKind.PermissionDenied => ConnectionTestStatus.AuthenticationFailed,
                CloudErrorKind.Network or CloudErrorKind.Timeout => ConnectionTestStatus.NetworkError,
                CloudErrorKind.RateLimited => ConnectionTestStatus.RateLimited,
                CloudErrorKind.DependencyMissing => ConnectionTestStatus.DependencyMissing,
                CloudErrorKind.NotFound => ConnectionTestStatus.NotConfigured,
                _ => ConnectionTestStatus.Error,
            };
            return new ConnectionTestResult(status, ex.Message, sw.Elapsed);
        }
    }

    public async Task<IReadOnlyList<CloudEntry>> ListAsync(string path, CancellationToken ct)
    {
        var dir = CloudPath.Normalize(path);
        var result = await _daemon.CallAsync("operations/list", new { fs = Fs, remote = Rel(dir), opt = new { noMimeType = false } }, ct).ConfigureAwait(false);
        var list = new List<CloudEntry>();
        if (result["list"] is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                var entry = ParseItem(item);
                if (entry != null)
                {
                    list.Add(entry);
                }
            }
        }

        return list;
    }

    public async Task<CloudEntry?> StatAsync(string path, CancellationToken ct)
    {
        var p = CloudPath.Normalize(path);
        if (p == CloudPath.Root)
        {
            return new CloudEntry(CloudPath.Root, string.Empty, true, 0, DateTimeOffset.UtcNow);
        }

        try
        {
            var result = await _daemon.CallAsync("operations/stat", new { fs = Fs, remote = Rel(p), opt = new { } }, ct).ConfigureAwait(false);
            return result["item"] is JsonObject item ? ParseItem(item) : null;
        }
        catch (CloudProviderException ex) when (ex.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct)
    {
        var response = await _daemon.GetObjectAsync(Fs, Rel(path), offset, length, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (offset > 0 && (int)response.StatusCode != 206)
        {
            // Backend ignored the range: skip to the offset so the data is still correct.
            _capabilities &= ~ProviderCapabilities.RangeRead;
            await SkipAsync(stream, offset, ct).ConfigureAwait(false);
        }

        return new ResponseStream(stream, response, length);
    }

    public async Task UploadAsync(string path, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var p = CloudPath.Normalize(path);
        var counting = new ProgressStream(content, progress);
        await _daemon.UploadStreamAsync(Fs, Rel(CloudPath.GetParent(p)), CloudPath.GetName(p), counting, ct).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string path, string localFile, IProgress<long>? progress, CancellationToken ct)
    {
        var full = Path.GetFullPath(localFile);
        var parameters = new Dictionary<string, object?>
        {
            ["srcFs"] = Path.GetDirectoryName(full),
            ["srcRemote"] = Path.GetFileName(full),
            ["dstFs"] = Fs,
            ["dstRemote"] = Rel(path),
        };
        await _daemon.RunJobAsync("operations/copyfile", parameters, progress, ct).ConfigureAwait(false);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        _daemon.CallAsync("operations/mkdir", new { fs = Fs, remote = Rel(path) }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) =>
        _daemon.CallAsync("operations/deletefile", new { fs = Fs, remote = Rel(path) }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) =>
        _daemon.CallAsync("operations/rmdir", new { fs = Fs, remote = Rel(path) }, ct);

    public async Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken ct)
    {
        if (!overwrite && await StatAsync(destinationPath, ct).ConfigureAwait(false) != null)
        {
            throw new CloudProviderException(CloudErrorKind.AlreadyExists, $"Destination exists: {destinationPath}");
        }

        if (isDirectory)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["srcFs"] = JoinFs(Rel(sourcePath)),
                ["dstFs"] = JoinFs(Rel(destinationPath)),
                ["createEmptySrcDirs"] = true,
                ["deleteEmptySrcDirs"] = true,
            };
            await _daemon.RunJobAsync("sync/move", parameters, null, ct).ConfigureAwait(false);
            // sync/move leaves the (now empty) source root behind on some backends.
            try
            {
                await DeleteDirectoryAsync(sourcePath, ct).ConfigureAwait(false);
            }
            catch (CloudProviderException ex) when (ex.Kind is CloudErrorKind.NotFound or CloudErrorKind.Unknown)
            {
            }

            return;
        }

        await _daemon.CallAsync(
            "operations/movefile",
            new { srcFs = Fs, srcRemote = Rel(sourcePath), dstFs = Fs, dstRemote = Rel(destinationPath) },
            ct,
            TimeSpan.FromMinutes(30)).ConfigureAwait(false);
    }

    public async Task<QuotaInfo?> GetQuotaAsync(CancellationToken ct)
    {
        try
        {
            var r = await _daemon.CallAsync("operations/about", new { fs = Fs }, ct).ConfigureAwait(false);
            return new QuotaInfo(GetLong(r, "total"), GetLong(r, "used"), GetLong(r, "free"));
        }
        catch (CloudProviderException ex) when (ex.Kind is CloudErrorKind.Unknown or CloudErrorKind.NotSupported)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string JoinFs(string rel)
    {
        if (string.IsNullOrEmpty(rel))
        {
            return Fs;
        }

        return string.IsNullOrEmpty(_root) ? $"{_remote}:{rel}" : $"{_remote}:{_root}/{rel}";
    }

    private static CloudEntry? ParseItem(JsonObject item)
    {
        var relPath = item["Path"]?.ToString();
        if (relPath == null)
        {
            return null;
        }

        var isDir = item["IsDir"]?.GetValue<bool>() ?? false;
        var size = item["Size"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : 0;
        var modified = DateTimeOffset.TryParse(item["ModTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
            ? m
            : DateTimeOffset.UnixEpoch;
        var name = item["Name"]?.ToString() ?? relPath.Split('/').Last();
        return new CloudEntry(
            CloudPath.Normalize(relPath),
            name,
            isDir,
            isDir ? 0 : Math.Max(0, size),
            modified,
            item["ID"]?.ToString(),
            item["MimeType"]?.ToString());
    }

    private static bool IsTrue(JsonObject o, string key) => o[key]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    private static long? GetLong(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : (o[key] is JsonValue d && d.TryGetValue<double>(out var dbl) ? (long)dbl : null);

    private static async Task SkipAsync(Stream s, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var n = await s.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            count -= n;
        }
    }
}
