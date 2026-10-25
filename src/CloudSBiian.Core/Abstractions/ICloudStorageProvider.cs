namespace CloudSBiian.Core.Abstractions;

/// <summary>
/// A file or folder on a cloud backend. Paths are always normalized cloud paths
/// ("/" rooted, "/" separated, see <see cref="Paths.CloudPath"/>).
/// </summary>
public sealed record CloudEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified,
    string? ETag = null,
    string? MimeType = null)
{
    /// <summary>
    /// Identity used for cache validation. Two entries with the same identity are
    /// assumed to have identical content.
    /// </summary>
    public string Identity => $"{Size}:{Modified.UtcTicks}:{ETag}";
}

/// <summary>Storage quota reported by the backend. Null fields mean "not reported".</summary>
public sealed record QuotaInfo(long? Total, long? Used, long? Free);

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    List = 1 << 0,
    Read = 1 << 1,
    /// <summary>Backend can return an arbitrary byte range without downloading the whole file.</summary>
    RangeRead = 1 << 2,
    Upload = 1 << 3,
    Delete = 1 << 4,
    Rename = 1 << 5,
    Move = 1 << 6,
    CreateDirectory = 1 << 7,
    Quota = 1 << 8,
    DirectoryMove = 1 << 9,

    ReadOnly = List | Read,
    Full = List | Read | RangeRead | Upload | Delete | Rename | Move | CreateDirectory | DirectoryMove,
}

public enum ConnectionTestStatus
{
    Ok,
    AuthenticationFailed,
    AuthenticationExpired,
    NetworkError,
    RateLimited,
    NotConfigured,
    DependencyMissing,
    Error,
}

public sealed record ConnectionTestResult(ConnectionTestStatus Status, string Message, TimeSpan Latency, string? Identity = null)
{
    public bool Success => Status == ConnectionTestStatus.Ok;
}

/// <summary>
/// Common adapter contract for every storage backend. Implementations must be thread safe:
/// the virtual drive calls them concurrently from many file system threads.
/// </summary>
public interface ICloudStorageProvider : IAsyncDisposable
{
    string AccountId { get; }

    ProviderKind Kind { get; }

    /// <summary>Capabilities actually available. May be refined after <see cref="ConnectAsync"/>.</summary>
    ProviderCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct);

    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct);

    Task<IReadOnlyList<CloudEntry>> ListAsync(string path, CancellationToken ct);

    /// <summary>Returns null when the path does not exist.</summary>
    Task<CloudEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>
    /// Opens a read stream starting at <paramref name="offset"/>. When <paramref name="length"/> is given
    /// the stream ends after that many bytes. Providers without <see cref="ProviderCapabilities.RangeRead"/>
    /// only accept offset 0 and a null length.
    /// </summary>
    Task<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct);

    Task UploadAsync(string path, Stream content, long length, IProgress<long>? progress, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);

    Task DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Deletes an empty directory.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken ct);

    Task<QuotaInfo?> GetQuotaAsync(CancellationToken ct);
}

/// <summary>Optional: providers that can upload directly from a local file (resumable/multipart internally).</summary>
public interface IFileUploadProvider
{
    Task UploadFileAsync(string path, string localFile, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>Optional: providers that can report the signed-in identity (e-mail / user).</summary>
public interface IIdentityProvider
{
    Task<string?> GetIdentityAsync(CancellationToken ct);
}
