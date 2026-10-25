using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Platform;
using CloudSBiian.Core.Security;

namespace CloudSBiian.CloudProviders.Rclone;

/// <summary>
/// Creates/reads/deletes remotes in the daemon's in-memory config. Remote names are derived from
/// the account id so two accounts of the same provider never share a remote or a session.
/// </summary>
public sealed class RcloneRemoteConfigurator
{
    private readonly RcloneDaemon _daemon;
    private readonly IAppLog _log;

    public RcloneRemoteConfigurator(RcloneDaemon daemon, IAppLog? log = null)
    {
        _daemon = daemon;
        _log = log ?? NullLog.Instance;
    }

    public static string RemoteNameFor(string accountId) => "csb_" + accountId;

    /// <summary>
    /// Creates the remote non-interactively. When rclone asks follow-up questions (OneDrive drive
    /// selection, etc.) the backend's own default answer is used, which is what interactive
    /// "rclone config" proposes too.
    /// </summary>
    public async Task CreateAsync(string accountId, string backend, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        await _daemon.EnsureStartedAsync(ct).ConfigureAwait(false);
        var name = RemoteNameFor(accountId);
        var p = new JsonObject();
        foreach (var (k, v) in parameters)
        {
            p[k] = v;
        }

        var request = new JsonObject
        {
            ["name"] = name,
            ["type"] = backend,
            ["parameters"] = p,
            ["opt"] = new JsonObject { ["nonInteractive"] = true, ["obscure"] = true },
        };
        var result = await _daemon.CallAsync("config/create", request, ct).ConfigureAwait(false);

        for (var round = 0; round < 12; round++)
        {
            var state = result["State"]?.ToString();
            var error = result["Error"]?.ToString();
            if (!string.IsNullOrEmpty(error))
            {
                throw new CloudProviderException(CloudErrorKind.AuthenticationFailed, $"rclone could not configure the remote: {error}");
            }

            if (string.IsNullOrEmpty(state))
            {
                return;
            }

            var option = result["Option"] as JsonObject;
            var answer = option?["DefaultStr"]?.ToString() ?? option?["Default"]?.ToString() ?? string.Empty;
            var optionName = option?["Name"]?.ToString() ?? "?";
            if (optionName == "config_token" || optionName.EndsWith("token", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(answer))
            {
                throw new CloudProviderException(CloudErrorKind.AuthenticationFailed,
                    "rclone needs an interactive login for this account. Use Connect/Reconnect to sign in in the browser.");
            }

            _log.Debug("rclone", $"config/create question '{optionName}' answered with backend default.", accountId);
            var cont = new JsonObject
            {
                ["name"] = name,
                ["parameters"] = new JsonObject(),
                ["opt"] = new JsonObject
                {
                    ["nonInteractive"] = true,
                    ["obscure"] = true,
                    ["continue"] = true,
                    ["state"] = state,
                    ["result"] = answer,
                },
            };
            result = await _daemon.CallAsync("config/update", cont, ct).ConfigureAwait(false);
        }

        throw new CloudProviderException(CloudErrorKind.Unknown, "rclone configuration did not complete.");
    }

    public async Task<Dictionary<string, string>> GetAsync(string accountId, CancellationToken ct)
    {
        var result = await _daemon.CallAsync("config/get", new { name = RemoteNameFor(accountId) }, ct).ConfigureAwait(false);
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in result)
        {
            if (v != null && k != "type")
            {
                dict[k] = v.ToString();
                if (k is "token" or "pass" or "password" or "cookie" or "secret_access_key" or "client_secret")
                {
                    SecretRedactor.Register(dict[k]);
                }
            }
        }

        return dict;
    }

    public async Task DeleteAsync(string accountId, CancellationToken ct)
    {
        if (!_daemon.IsRunning)
        {
            return;
        }

        try
        {
            await _daemon.CallAsync("config/delete", new { name = RemoteNameFor(accountId) }, ct).ConfigureAwait(false);
        }
        catch (CloudProviderException ex)
        {
            _log.Debug("rclone", $"config/delete ignored: {ex.Message}", accountId);
        }
    }
}

/// <summary>
/// Runs "rclone authorize &lt;backend&gt;", which opens the user's browser on the provider's official
/// OAuth consent page and returns the token on stdout. CloudSBiian never sees the user's password.
/// </summary>
public sealed class RcloneAuthorizer
{
    private const string StartMarker = "--->";
    private const string EndMarker = "<---End paste";

    private readonly string _rclonePath;

    public RcloneAuthorizer(string rclonePath) => _rclonePath = rclonePath;

    public async Task<string> AuthorizeAsync(string backend, IReadOnlyDictionary<string, string>? clientOverrides, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_rclonePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("authorize");
        psi.ArgumentList.Add(backend);
        if (clientOverrides != null &&
            clientOverrides.TryGetValue("client_id", out var id) && !string.IsNullOrWhiteSpace(id) &&
            clientOverrides.TryGetValue("client_secret", out var secret) && !string.IsNullOrWhiteSpace(secret))
        {
            psi.ArgumentList.Add(id);
            psi.ArgumentList.Add(secret);
        }

        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(string.Empty);

        using var process = Process.Start(psi) ?? throw new CloudProviderException(CloudErrorKind.DependencyMissing, "Could not start rclone authorize.");
        ChildProcessJob.Attach(process);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, stdout);
        var errTask = PumpAsync(process.StandardError, stderr);
        using var reg = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var text = stdout.ToString();
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (process.ExitCode != 0 || start < 0 || end < 0 || end <= start)
        {
            var err = SecretRedactor.Redact(stderr.ToString().Trim());
            throw new CloudProviderException(CloudErrorKind.AuthenticationFailed,
                $"Browser sign-in did not complete (rclone exit code {process.ExitCode}). {LastLine(err)}");
        }

        var token = text[(start + StartMarker.Length)..end].Trim();
        SecretRedactor.Register(token);
        return token;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sb)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            sb.AppendLine(line);
        }
    }

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
}
