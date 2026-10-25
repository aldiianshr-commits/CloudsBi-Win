using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.CloudProviders.WebDav;

/// <summary>
/// Native WebDAV client (RFC 4918) with HTTP Range reads and RFC 4331 quota properties.
/// </summary>
public sealed class WebDavProvider : ICloudStorageProvider, IFileUploadProvider
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod MoveMethod = new("MOVE");

    private const string PropfindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/><d:getcontenttype/>" +
        "</d:prop></d:propfind>";

    private const string QuotaBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:quota-available-bytes/><d:quota-used-bytes/></d:prop></d:propfind>";

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _basePath;
    private ProviderCapabilities _capabilities = ProviderCapabilities.Full;

    public WebDavProvider(string accountId, Uri baseUri, string? user, string? password, HttpMessageHandler? handler = null)
    {
        AccountId = accountId;
        var b = baseUri.ToString();
        _base = new Uri(b.EndsWith('/') ? b : b + "/");
        _basePath = _base.AbsolutePath;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrEmpty(user))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudSBiian/0.1");
    }

    public string AccountId { get; }

    public ProviderKind Kind => ProviderKind.WebDav;

    public ProviderCapabilities Capabilities => _capabilities;

    private Uri UriFor(string path, bool directory = false)
    {
        var p = CloudPath.Normalize(path);
        var rel = string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        if (directory && rel.Length > 0)
        {
            rel += "/";
        }

        return new Uri(_base, rel);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, _base);
        using var res = await SendAsync(req, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await EnsureAsync(res, "/").ConfigureAwait(false);
        if (await GetQuotaAsync(ct).ConfigureAwait(false) != null)
        {
            _capabilities |= ProviderCapabilities.Quota;
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await ListAsync(CloudPath.Root, ct).ConfigureAwait(false);
            return new ConnectionTestResult(ConnectionTestStatus.Ok, "Connected.", sw.Elapsed);
        }
        catch (CloudProviderException ex)
        {
            var status = ex.Kind switch
            {
                CloudErrorKind.AuthenticationFailed or CloudErrorKind.AuthenticationExpired => ConnectionTestStatus.AuthenticationFailed,
                CloudErrorKind.Network or CloudErrorKind.Timeout => ConnectionTestStatus.NetworkError,
                CloudErrorKind.RateLimited => ConnectionTestStatus.RateLimited,
                _ => ConnectionTestStatus.Error,
            };
            return new ConnectionTestResult(status, ex.Message, sw.Elapsed);
        }
    }

    public async Task<IReadOnlyList<CloudEntry>> ListAsync(string path, CancellationToken ct)
    {
        var dir = CloudPath.Normalize(path);
        var entries = await PropfindAsync(UriFor(dir, directory: true), "1", PropfindBody, dir, ct).ConfigureAwait(false);
        return entries
            .Where(e => !string.Equals(e.Path, dir, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<CloudEntry?> StatAsync(string path, CancellationToken ct)
    {
        var p = CloudPath.Normalize(path);
        try
        {
            var entries = await PropfindAsync(UriFor(p), "0", PropfindBody, p, ct).ConfigureAwait(false);
            return entries.FirstOrDefault();
        }
        catch (CloudProviderException ex) when (ex.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, UriFor(path));
        if (offset > 0 || length.HasValue)
        {
            req.Headers.Range = new RangeHeaderValue(offset, length.HasValue ? offset + length.Value - 1 : null);
        }

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            req.Dispose();
            throw new CloudProviderException(CloudErrorKind.Network, $"GET {path} failed: {ex.Message}", ex);
        }

        if (!res.IsSuccessStatusCode)
        {
            try
            {
                await EnsureAsync(res, path).ConfigureAwait(false);
            }
            finally
            {
                res.Dispose();
                req.Dispose();
            }
        }

        var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (offset > 0 && res.StatusCode != HttpStatusCode.PartialContent)
        {
            _capabilities &= ~ProviderCapabilities.RangeRead;
            var skip = new byte[81920];
            var remaining = offset;
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, remaining)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                remaining -= n;
            }
        }

        return new ResponseStream(stream, new CompositeDisposable(res, req), length);
    }

    public async Task UploadAsync(string path, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, UriFor(path))
        {
            Content = new StreamContent(new ProgressStream(content, progress), 1024 * 1024),
        };
        req.Content.Headers.ContentLength = length;
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await SendAsync(req, ct, null).ConfigureAwait(false);
        await EnsureAsync(res, path).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string path, string localFile, IProgress<long>? progress, CancellationToken ct)
    {
        await using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await UploadAsync(path, fs, fs.Length, progress, ct).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(Mkcol, UriFor(path, directory: true));
        using var res = await SendAsync(req, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            throw new CloudProviderException(CloudErrorKind.AlreadyExists, $"Already exists: {path}");
        }

        await EnsureAsync(res, path).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, UriFor(path));
        using var res = await SendAsync(req, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await EnsureAsync(res, path).ConfigureAwait(false);
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        // WebDAV DELETE on a collection is recursive, so refuse non-empty directories explicitly.
        var children = await ListAsync(path, ct).ConfigureAwait(false);
        if (children.Count > 0)
        {
            throw new CloudProviderException(CloudErrorKind.NotEmpty, $"Directory not empty: {path}");
        }

        using var req = new HttpRequestMessage(HttpMethod.Delete, UriFor(path, directory: true));
        using var res = await SendAsync(req, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await EnsureAsync(res, path).ConfigureAwait(false);
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool isDirectory, bool overwrite, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(MoveMethod, UriFor(sourcePath, isDirectory));
        req.Headers.Add("Destination", UriFor(destinationPath, isDirectory).AbsoluteUri);
        req.Headers.Add("Overwrite", overwrite ? "T" : "F");
        using var res = await SendAsync(req, ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new CloudProviderException(CloudErrorKind.AlreadyExists, $"Destination exists: {destinationPath}");
        }

        await EnsureAsync(res, sourcePath).ConfigureAwait(false);
    }

    public async Task<QuotaInfo?> GetQuotaAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(Propfind, _base) { Content = new StringContent(QuotaBody, Encoding.UTF8, "application/xml") };
            req.Headers.Add("Depth", "0");
            using var res = await SendAsync(req, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return null;
            }

            var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var avail = ParseLong(xml.Descendants(Dav + "quota-available-bytes").FirstOrDefault()?.Value);
            var used = ParseLong(xml.Descendants(Dav + "quota-used-bytes").FirstOrDefault()?.Value);
            if (avail == null && used == null)
            {
                return null;
            }

            return new QuotaInfo(avail.HasValue && used.HasValue ? avail + used : null, used, avail);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or CloudProviderException or HttpRequestException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<List<CloudEntry>> PropfindAsync(Uri uri, string depth, string body, string requestedPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(Propfind, uri) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        req.Headers.Add("Depth", depth);
        using var res = await SendAsync(req, ct, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        await EnsureAsync(res, requestedPath).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        XDocument xml;
        try
        {
            xml = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CloudProviderException(CloudErrorKind.Unknown, "Server returned an invalid PROPFIND response.", ex);
        }

        var list = new List<CloudEntry>();
        foreach (var response in xml.Descendants(Dav + "response"))
        {
            var href = response.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var prop = response.Elements(Dav + "propstat")
                .Where(ps => (ps.Element(Dav + "status")?.Value ?? "200").Contains(" 200", StringComparison.Ordinal) ||
                             ps.Element(Dav + "status") == null)
                .Select(ps => ps.Element(Dav + "prop"))
                .FirstOrDefault(p => p != null);
            if (prop == null)
            {
                continue;
            }

            var path = HrefToPath(href);
            if (path == null)
            {
                continue;
            }

            var isDir = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null;
            var size = ParseLong(prop.Element(Dav + "getcontentlength")?.Value) ?? 0;
            var modified = DateTimeOffset.TryParse(prop.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
                ? m
                : DateTimeOffset.UnixEpoch;
            var etag = prop.Element(Dav + "getetag")?.Value?.Trim('"');
            list.Add(new CloudEntry(path, CloudPath.GetName(path), isDir, isDir ? 0 : size, modified, etag, prop.Element(Dav + "getcontenttype")?.Value));
        }

        return list;
    }

    private string? HrefToPath(string href)
    {
        string absPath;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
        {
            absPath = abs.AbsolutePath;
        }
        else
        {
            absPath = href.Split('?')[0];
        }

        var decoded = Uri.UnescapeDataString(absPath);
        var basePath = Uri.UnescapeDataString(_basePath);
        if (!decoded.StartsWith(basePath, StringComparison.Ordinal))
        {
            if (!decoded.TrimEnd('/').Equals(basePath.TrimEnd('/'), StringComparison.Ordinal))
            {
                return null;
            }

            return CloudPath.Root;
        }

        try
        {
            return CloudPath.Normalize(decoded[basePath.Length..]);
        }
        catch (PathValidationException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, TimeSpan? timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudProviderException(CloudErrorKind.Network, $"{req.Method} {req.RequestUri?.AbsolutePath} failed: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudProviderException(CloudErrorKind.Timeout, $"{req.Method} {req.RequestUri?.AbsolutePath} timed out.");
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage res, string path)
    {
        if (res.IsSuccessStatusCode || (int)res.StatusCode == 207)
        {
            return;
        }

        var code = (int)res.StatusCode;
        TimeSpan? retry = res.Headers.RetryAfter?.Delta;
        string body;
        try
        {
            body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            body = string.Empty;
        }

        if (body.Length > 300)
        {
            body = body[..300];
        }

        var kind = code switch
        {
            401 => CloudErrorKind.AuthenticationFailed,
            403 => CloudErrorKind.PermissionDenied,
            404 => CloudErrorKind.NotFound,
            405 => CloudErrorKind.NotSupported,
            409 => CloudErrorKind.Conflict,
            412 => CloudErrorKind.AlreadyExists,
            423 => CloudErrorKind.Conflict,
            429 => CloudErrorKind.RateLimited,
            507 => CloudErrorKind.InsufficientStorage,
            >= 500 => CloudErrorKind.Network,
            _ => CloudErrorKind.Unknown,
        };
        throw new CloudProviderException(kind, $"HTTP {code} for {path}. {body}".Trim(), null, retry);
    }

    private static long? ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items)
            {
                i.Dispose();
            }
        }
    }
}
