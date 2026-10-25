using System.Collections.Concurrent;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.CloudProviders.Mock;

/// <summary>
/// Deterministic in-memory backend for automated tests: configurable latency, range support,
/// injected failures (network, rate limit, expired session) and call counters.
/// Results obtained with this provider are reported separately from real-provider tests.
/// </summary>
public sealed class InMemoryProvider : ICloudStorageProvider
{
    private readonly ConcurrentDictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _failNext;
    private CloudErrorKind _failKind = CloudErrorKind.Network;

    public InMemoryProvider(string accountId, bool supportsRange = true)
    {
        AccountId = accountId;
        SupportsRange = supportsRange;
        _nodes[CloudPath.Root] = new Node(true, Array.Empty<byte>(), DateTimeOffset.UtcNow, 0);
    }

    public string AccountId { get; }

    public ProviderKind Kind => ProviderKind.Mock;

    public bool SupportsRange { get; set; }

    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    public bool SessionExpired { get; set; }

    public bool Offline { get; set; }

    public QuotaInfo? Quota { get; set; } = new QuotaInfo(100L * 1024 * 1024 * 1024, 0, 100L * 1024 * 1024 * 1024);

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Full & ~(SupportsRange ? ProviderCapabilities.None : ProviderCapabilities.RangeRead) | ProviderCapabilities.Quota;

    public int ReadCalls;
    public long BytesServed;
    public int ListCalls;
    public int UploadCalls;

    /// <summary>The next <paramref name="count"/> operations fail with <paramref name="kind"/>.</summary>
    public void FailNext(int count, CloudErrorKind kind)
    {
        lock (_gate)
        {
            _failNext = count;
            _failKind = kind;
        }
    }

    public void AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        var p = CloudPath.Normalize(path);
        EnsureParents(p);
        _nodes[p] = new Node(false, content, modified ?? DateTimeOffset.UtcNow, Interlocked.Increment(ref _version));
    }

    public void AddDirectory(string path)
    {
        var p = CloudPath.Normalize(path);
        EnsureParents(p);
        _nodes.TryAdd(p, new Node(true, Array.Empty<byte>(), DateTimeOffset.UtcNow, 0));
    }

    public byte[]? GetContent(string path) =>
        _nodes.TryGetValue(CloudPath.Normalize(path), out var n) && !n.IsDirectory ? n.Content : null;

    public bool Exists(string path) => _nodes.ContainsKey(CloudPath.Normalize(path));

    private long _version;

    public Task ConnectAsync(CancellationToken ct) => Gate(ct);

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await Gate(ct).ConfigureAwait(false);
            return new ConnectionTestResult(ConnectionTestStatus.Ok, "OK", Latency, "mock@" + AccountId);
        }
        catch (CloudProviderException ex)
        {
            var status = ex.Kind switch
            {
                CloudErrorKind.AuthenticationExpired => ConnectionTestStatus.AuthenticationExpired,
                CloudErrorKind.RateLimited => ConnectionTestStatus.RateLimited,
                CloudErrorKind.Network => ConnectionTestStatus.NetworkError,
                _ => ConnectionTestStatus.Error,
            };
            return new ConnectionTestResult(status, ex.Message, Latency);
        }
    }

    public async Task<IReadOnlyList<CloudEntry>> ListAsync(string path, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        Interlocked.Increment(ref ListCalls);
        var p = CloudPath.Normalize(path);
        if (!_nodes.TryGetValue(p, out var dir) || !dir.IsDirectory)
        {
            throw CloudProviderException.NotFound(p);
        }

        return _nodes
            .Where(kv => kv.Key != p && CloudPath.GetParent(kv.Key) == p)
            .Select(kv => ToEntry(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<CloudEntry?> StatAsync(string path, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        var p = CloudPath.Normalize(path);
        return _nodes.TryGetValue(p, out var n) ? ToEntry(p, n) : null;
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        Interlocked.Increment(ref ReadCalls);
        var p = CloudPath.Normalize(path);
        if (!_nodes.TryGetValue(p, out var n) || n.IsDirectory)
        {
            throw CloudProviderException.NotFound(p);
        }

        if (!SupportsRange && (offset != 0 || length.HasValue))
        {
            throw new CloudProviderException(CloudErrorKind.NotSupported, "Range reads not supported by this mock.");
        }

        var start = (int)Math.Min(offset, n.Content.Length);
        var count = (int)Math.Min(length ?? long.MaxValue, n.Content.Length - start);
        Interlocked.Add(ref BytesServed, count);
        return new MemoryStream(n.Content, start, count, writable: false);
    }

    public async Task UploadAsync(string path, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        Interlocked.Increment(ref UploadCalls);
        var ms = new MemoryStream();
        await new ProgressStream(content, progress).CopyToAsync(ms, ct).ConfigureAwait(false);
        AddFile(path, ms.ToArray());
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        AddDirectory(path);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        var p = CloudPath.Normalize(path);
        if (!_nodes.TryGetValue(p, out var n) || n.IsDirectory || !_nodes.TryRemove(p, out _))
        {
            throw CloudProviderException.NotFound(p);
        }
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        var p = CloudPath.Normalize(path);
        if (!_nodes.TryGetValue(p, out var n) || !n.IsDirectory)
        {
            throw CloudProviderException.NotFound(p);
        }

        if (_nodes.Keys.Any(k => k != p && CloudPath.GetParent(k) == p))
        {
            throw new CloudProviderException(CloudErrorKind.NotEmpty, "Directory not empty.");
        }

        _nodes.TryRemove(p, out _);
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        var src = CloudPath.Normalize(sourcePath);
        var dst = CloudPath.Normalize(destinationPath);
        lock (_gate)
        {
            if (!_nodes.ContainsKey(src))
            {
                throw CloudProviderException.NotFound(src);
            }

            if (_nodes.ContainsKey(dst) && (!overwrite || isDirectory))
            {
                throw new CloudProviderException(CloudErrorKind.AlreadyExists, "Destination exists.");
            }

            foreach (var key in _nodes.Keys.Where(k => CloudPath.IsSameOrDescendant(src, k, StringComparison.Ordinal)).ToList())
            {
                if (_nodes.TryRemove(key, out var node))
                {
                    _nodes[CloudPath.Rebase(key, src, dst)] = node;
                }
            }
        }
    }

    public async Task<QuotaInfo?> GetQuotaAsync(CancellationToken ct)
    {
        await Gate(ct).ConfigureAwait(false);
        return Quota;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task Gate(CancellationToken ct)
    {
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (Offline)
        {
            throw new CloudProviderException(CloudErrorKind.Network, "Simulated offline.");
        }

        if (SessionExpired)
        {
            throw new CloudProviderException(CloudErrorKind.AuthenticationExpired, "Simulated expired session.");
        }

        lock (_gate)
        {
            if (_failNext > 0)
            {
                _failNext--;
                throw new CloudProviderException(_failKind, $"Simulated {_failKind}.", null,
                    _failKind == CloudErrorKind.RateLimited ? TimeSpan.FromMilliseconds(50) : null);
            }
        }
    }

    private void EnsureParents(string p)
    {
        var parent = CloudPath.GetParent(p);
        while (parent != CloudPath.Root && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node(true, Array.Empty<byte>(), DateTimeOffset.UtcNow, 0);
            parent = CloudPath.GetParent(parent);
        }
    }

    private static CloudEntry ToEntry(string path, Node n) =>
        new(path, CloudPath.GetName(path), n.IsDirectory, n.IsDirectory ? 0 : n.Content.Length, n.Modified, n.IsDirectory ? null : "v" + n.Version);

    private sealed record Node(bool IsDirectory, byte[] Content, DateTimeOffset Modified, long Version);
}
