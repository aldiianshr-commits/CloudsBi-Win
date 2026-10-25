using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Platform;
using CloudSBiian.Core.Security;

namespace CloudSBiian.CloudProviders.Rclone;

/// <summary>Error returned by the rclone remote-control API.</summary>
public sealed class RcloneRcException : CloudProviderException
{
    public RcloneRcException(CloudErrorKind kind, string message, int httpStatus)
        : base(kind, message)
    {
        HttpStatus = httpStatus;
    }

    public int HttpStatus { get; }
}

/// <summary>
/// Runs a private "rclone rcd" instance bound to 127.0.0.1 on a random port with a random
/// password, and talks to it through the documented rc HTTP API. All accounts' remotes live
/// in rclone's in-memory config: secrets are injected from the DPAPI store at connect time and
/// never written to an rclone.conf file by CloudSBiian.
/// </summary>
public sealed class RcloneDaemon : IAsyncDisposable
{
    private const string User = "cloudsbiian";
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private HttpClient? _http;
    private string? _password;

    public RcloneDaemon(string executablePath, IAppLog? log = null)
    {
        ExecutablePath = executablePath;
        _log = log ?? NullLog.Instance;
    }

    public string ExecutablePath { get; }

    public Uri? BaseUri { get; private set; }

    public bool IsRunning => _process is { HasExited: false } && _http != null;

    public string? Version { get; private set; }

    public event EventHandler? Exited;

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            return;
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            await StopInternalAsync().ConfigureAwait(false);
            var port = GetFreePort();
            _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            SecretRedactor.Register(_password);

            var psi = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("rcd");
            psi.ArgumentList.Add("--rc-addr");
            psi.ArgumentList.Add($"127.0.0.1:{port}");
            psi.ArgumentList.Add("--rc-serve");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(string.Empty);
            psi.ArgumentList.Add("--log-level");
            psi.ArgumentList.Add("NOTICE");
            // Credentials through the environment so they do not show up in process listings.
            psi.Environment["RCLONE_RC_USER"] = User;
            psi.Environment["RCLONE_RC_PASS"] = _password;
            psi.Environment.Remove("RCLONE_CONFIG");

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => LogOutput(e.Data);
            process.ErrorDataReceived += (_, e) => LogOutput(e.Data);
            process.Exited += (_, _) =>
            {
                _log.Warn("rclone", $"rclone daemon exited (code {SafeExitCode(process)}).");
                Exited?.Invoke(this, EventArgs.Empty);
            };
            if (!process.Start())
            {
                throw new CloudProviderException(CloudErrorKind.DependencyMissing, "Could not start rclone.");
            }

            ChildProcessJob.Attach(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{_password}")));
            BaseUri = new Uri($"http://127.0.0.1:{port}/");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new CloudProviderException(CloudErrorKind.DependencyMissing, $"rclone exited during startup (code {SafeExitCode(process)}). Check the rclone path in Settings.");
                }

                try
                {
                    var v = await CallAsync("core/version", null, ct, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    Version = v["version"]?.GetValue<string>();
                    _log.Info("rclone", $"rclone daemon ready ({Version}) on 127.0.0.1:{port}.");
                    return;
                }
                catch (CloudProviderException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Calls an rc method with a JSON body and returns the JSON result object.</summary>
    public async Task<JsonObject> CallAsync(string method, object? parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        var http = _http ?? throw new CloudProviderException(CloudErrorKind.DependencyMissing, "rclone daemon is not running.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var json = parameters == null ? "{}" : JsonSerializer.Serialize(parameters);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(BaseUri!, method), content, cts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudProviderException(CloudErrorKind.DependencyMissing, $"rclone daemon is not reachable ({method}).", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudProviderException(CloudErrorKind.Timeout, $"rclone call '{method}' timed out.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonObject? obj = null;
            try
            {
                obj = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = obj?["error"]?.ToString() ?? body;
                throw new RcloneRcException(ClassifyError(error, (int)response.StatusCode), $"{method}: {error}", (int)response.StatusCode);
            }

            return obj ?? new JsonObject();
        }
    }

    /// <summary>Opens an object through --rc-serve with an optional HTTP Range.</summary>
    public async Task<HttpResponseMessage> GetObjectAsync(string fs, string remotePath, long offset, long? length, CancellationToken ct)
    {
        var http = _http ?? throw new CloudProviderException(CloudErrorKind.DependencyMissing, "rclone daemon is not running.");
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var url = new Uri(BaseUri!, "[" + Uri.EscapeDataString(fs) + "]/" + string.Join('/', segments));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0 || length.HasValue)
        {
            request.Headers.Range = new RangeHeaderValue(offset, length.HasValue ? offset + length.Value - 1 : null);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            request.Dispose();
            throw new CloudProviderException(CloudErrorKind.DependencyMissing, "rclone daemon is not reachable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            response.Dispose();
            request.Dispose();
            if (status == 404)
            {
                throw CloudProviderException.NotFound(remotePath);
            }

            if (status == 416)
            {
                throw new CloudProviderException(CloudErrorKind.InvalidPath, $"Requested range not satisfiable for {remotePath}.");
            }

            throw new RcloneRcException(ClassifyError(body, status), $"read {remotePath}: {body}", status);
        }

        return response;
    }

    /// <summary>Uploads through the documented operations/uploadfile multipart endpoint.</summary>
    public async Task UploadStreamAsync(string fs, string remoteDir, string fileName, Stream content, CancellationToken ct)
    {
        var http = _http ?? throw new CloudProviderException(CloudErrorKind.DependencyMissing, "rclone daemon is not running.");
        var url = new Uri(BaseUri!, $"operations/uploadfile?fs={Uri.EscapeDataString(fs)}&remote={Uri.EscapeDataString(remoteDir)}");
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(content, 1024 * 1024);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file0", fileName);
        using var response = await http.PostAsync(url, form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RcloneRcException(ClassifyError(body, (int)response.StatusCode), $"upload {fileName}: {body}", (int)response.StatusCode);
        }
    }

    /// <summary>Starts an async rc job and waits for it, reporting transferred bytes.</summary>
    public async Task RunJobAsync(string method, Dictionary<string, object?> parameters, IProgress<long>? progress, CancellationToken ct)
    {
        parameters["_async"] = true;
        var start = await CallAsync(method, parameters, ct).ConfigureAwait(false);
        var jobId = start["jobid"]?.GetValue<long>() ?? throw new CloudProviderException(CloudErrorKind.Unknown, $"{method} did not return a job id.");
        try
        {
            while (true)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var status = await CallAsync("job/status", new { jobid = jobId }, ct).ConfigureAwait(false);
                if (progress != null)
                {
                    try
                    {
                        var stats = await CallAsync("core/stats", new { group = $"job/{jobId}" }, ct).ConfigureAwait(false);
                        var bytes = stats["bytes"]?.GetValue<long>() ?? 0;
                        progress.Report(bytes);
                    }
                    catch (CloudProviderException)
                    {
                        // Stats are best effort.
                    }
                }

                if (status["finished"]?.GetValue<bool>() == true)
                {
                    if (status["success"]?.GetValue<bool>() != true)
                    {
                        var error = status["error"]?.ToString() ?? "unknown error";
                        throw new RcloneRcException(ClassifyError(error, 500), $"{method}: {error}", 500);
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                await CallAsync("job/stop", new { jobid = jobId }, CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
            }

            throw;
        }
    }

    public static CloudErrorKind ClassifyError(string? error, int httpStatus)
    {
        var e = (error ?? string.Empty).ToLowerInvariant();
        if (e.Contains("object not found") || e.Contains("directory not found") || e.Contains("not found") || e.Contains("no such file") || httpStatus == 404)
        {
            return CloudErrorKind.NotFound;
        }

        if (e.Contains("directory not empty"))
        {
            return CloudErrorKind.NotEmpty;
        }

        if (e.Contains("already exists"))
        {
            return CloudErrorKind.AlreadyExists;
        }

        if (e.Contains("ratelimit") || e.Contains("rate limit") || e.Contains("429") || e.Contains("too many requests") || e.Contains("quota exceeded for quota"))
        {
            return CloudErrorKind.RateLimited;
        }

        if (e.Contains("invalid_grant") || e.Contains("token expired") || e.Contains("expired") && e.Contains("token") || e.Contains("unauthorized") || e.Contains("401"))
        {
            return CloudErrorKind.AuthenticationExpired;
        }

        if (e.Contains("permission denied") || e.Contains("forbidden") || e.Contains("403") || e.Contains("access denied"))
        {
            return CloudErrorKind.PermissionDenied;
        }

        if (e.Contains("insufficient") || e.Contains("storage quota") || e.Contains("no space"))
        {
            return CloudErrorKind.InsufficientStorage;
        }

        if (e.Contains("didn't find section in config file") || e.Contains("couldn't find type"))
        {
            return CloudErrorKind.DependencyMissing;
        }

        if (e.Contains("timeout") || e.Contains("deadline exceeded"))
        {
            return CloudErrorKind.Timeout;
        }

        if (e.Contains("connection") || e.Contains("dial tcp") || e.Contains("no such host") || e.Contains("network"))
        {
            return CloudErrorKind.Network;
        }

        return CloudErrorKind.Unknown;
    }

    private void LogOutput(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            var level = line.Contains("ERROR", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Debug;
            _log.Write(level, "rclone", line);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private async Task StopInternalAsync()
    {
        var p = _process;
        _process = null;
        if (p != null)
        {
            try
            {
                if (!p.HasExited && _http != null)
                {
                    try
                    {
                        await CallAsync("core/quit", null, CancellationToken.None, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (CloudProviderException)
                    {
                    }

                    if (!p.WaitForExit(3000))
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        _http?.Dispose();
        _http = null;
        SecretRedactor.Unregister(null);
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }
}
