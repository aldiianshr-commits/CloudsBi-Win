using System.Collections.Concurrent;
using CloudSBiian.CloudProviders.Local;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.CloudProviders.WebDav;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Security;

namespace CloudSBiian.CloudProviders;

/// <summary>Live state of one account. Each account is independent: its own provider, session and errors.</summary>
public sealed class AccountRuntime
{
    internal AccountRuntime(CloudAccount account) => Account = account;

    public CloudAccount Account { get; internal set; }

    public string Id => Account.Id;

    public AccountConnectionState State { get; internal set; } = AccountConnectionState.Disconnected;

    public string? LastError { get; internal set; }

    public ConnectionTestResult? LastTest { get; internal set; }

    public QuotaInfo? Quota { get; internal set; }

    public DateTimeOffset? ConnectedAt { get; internal set; }

    public ICloudStorageProvider? Provider { get; internal set; }

    public ProviderDescriptor Descriptor => ProviderRegistry.Get(Account.Kind);

    public bool IsConnected => State == AccountConnectionState.Connected && Provider != null;

    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class AccountManager : IAsyncDisposable, IProviderResolver
{
    public const string SecretToken = "token";
    public const string SecretPassword = "password";
    public const string OptionPath = "path";
    public const string OptionUrl = "url";
    public const string OptionUser = "user";

    private readonly AccountStore _store;
    private readonly ISecretStore _secrets;
    private readonly Func<string?> _rclonePath;
    private readonly IAppLog _log;
    private readonly ConcurrentDictionary<string, AccountRuntime> _runtimes = new();
    private readonly SemaphoreSlim _rcloneGate = new(1, 1);
    private RcloneDaemon? _daemon;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly Dictionary<string, (Version? Version, string? Line, string? Error)> _versionCache = new(StringComparer.OrdinalIgnoreCase);
    private RcloneBackendCatalog? _catalog;
    private string? _daemonPath;
    private RcloneStatus _rcloneStatus = RcloneStatus.NotChecked;

    public AccountManager(AccountStore store, ISecretStore secrets, Func<string?> rclonePath, IAppLog? log = null)
    {
        _store = store;
        _secrets = secrets;
        _rclonePath = rclonePath;
        _log = log ?? NullLog.Instance;
        foreach (var a in store.GetAll())
        {
            _runtimes[a.Id] = new AccountRuntime(a);
        }
    }

    /// <summary>Hook for automated tests: returns an in-memory provider for an account (null = normal behaviour).</summary>
    public Func<CloudAccount, ICloudStorageProvider?>? MockFactory { get; set; }

    /// <summary>How rclone is located (replaceable in tests). Argument: the path set in Settings.</summary>
    public Func<string?, RcloneLocation> RcloneLocate { get; set; } = RcloneLocator.Locate;

    public event EventHandler<AccountRuntime>? RuntimeChanged;

    public event EventHandler? AccountsChanged;

    public IReadOnlyList<AccountRuntime> Runtimes =>
        _runtimes.Values.OrderBy(r => r.Account.SortOrder).ThenBy(r => r.Account.CreatedAt).ToList();

    public AccountRuntime? Get(string accountId) => _runtimes.TryGetValue(accountId, out var r) ? r : null;

    public ICloudStorageProvider? TryGetProvider(string accountId) =>
        _runtimes.TryGetValue(accountId, out var r) && r.IsConnected ? r.Provider : null;

    public RcloneBackendCatalog? Catalog => _catalog;

    public string? RcloneVersion => _daemon?.Version;

    /// <summary>Path of the rclone that is (or would be) used: the checked one, else the first one found on disk.</summary>
    public string? RcloneExecutable => _rcloneStatus.IsReady ? _rcloneStatus.ExecutablePath : RcloneLocate(_rclonePath()).Path;

    /// <summary>Result of the last rclone dependency check (see <see cref="CheckRcloneAsync"/>).</summary>
    public RcloneStatus RcloneStatus => _rcloneStatus;

    /// <summary>Raised whenever the rclone dependency state changes (any thread).</summary>
    public event EventHandler<RcloneStatus>? RcloneStatusChanged;

    /// <summary>Starts rclone (if usable) and reads its backend catalog. Returns null when rclone is not usable; see <see cref="RcloneStatus"/>.</summary>
    public async Task<RcloneBackendCatalog?> GetCatalogAsync(CancellationToken ct)
    {
        if (_catalog != null && _daemon is { IsRunning: true } && _rcloneStatus.IsReady)
        {
            return _catalog;
        }

        var status = await CheckRcloneAsync(ct).ConfigureAwait(false);
        return status.IsReady ? _catalog : null;
    }

    /// <summary>
    /// Real dependency check: locate rclone (Settings path, bundled copy, CloudSBiian's own
    /// download, installed CloudSBiian, package managers, PATH), run "rclone version", check the
    /// minimum version, start the rc daemon and read the backend list. The first usable
    /// executable wins; a path chosen in Settings is used exclusively when it exists.
    /// </summary>
    public async Task<RcloneStatus> CheckRcloneAsync(CancellationToken ct)
    {
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _rcloneStatus;
            SetRcloneStatus(previous with { State = RcloneState.Checking, Message = "Checking rclone…" });
            var location = RcloneLocate(_rclonePath());
            var existing = location.Searched.Where(c => c.Exists).ToList();
            if (existing.Count > 0 && existing[0].Source == RcloneLocator.SettingsSource)
            {
                existing = [existing[0]];
            }

            if (existing.Count == 0)
            {
                await StopDaemonAsync().ConfigureAwait(false);
                var msg = $"rclone was not found in any of the {location.Searched.Count} locations checked (bundled folder, CloudSBiian tools folder, Program Files, winget, Scoop, Chocolatey, PATH).";
                if (location.ConfiguredPathProblem != null)
                {
                    msg = location.ConfiguredPathProblem + " " + msg;
                }

                _log.Warn("rclone", msg);
                return SetRcloneStatus(new RcloneStatus(RcloneState.Missing, null, null, null, 0, location.Searched, msg, DateTimeOffset.Now));
            }

            string? firstError = null;
            RcloneCandidate? firstCandidate = null;
            foreach (var candidate in existing)
            {
                var (version, line, error) = await ValidateCachedAsync(candidate.Path, ct).ConfigureAwait(false);
                if (error != null)
                {
                    _log.Warn("rclone", $"Skipping {candidate.Path} ({candidate.Source}): {error}");
                    firstError ??= error;
                    firstCandidate ??= candidate;
                    continue;
                }

                try
                {
                    var daemon = await GetDaemonAsync(candidate.Path, ct).ConfigureAwait(false);
                    _catalog ??= await RcloneBackendCatalog.LoadAsync(daemon, ct).ConfigureAwait(false);
                    var ready = new RcloneStatus(RcloneState.Ready, candidate.Path, candidate.Source, daemon.Version ?? line,
                        _catalog.Backends.Count, location.Searched,
                        $"rclone {daemon.Version ?? version?.ToString()} ({candidate.Source}) is running; {_catalog.Backends.Count} backends available." +
                        (location.ConfiguredPathProblem == null ? string.Empty : " Note: " + location.ConfiguredPathProblem + " — using the copy found automatically."),
                        DateTimeOffset.Now);
                    _log.Info("rclone", $"{ready.Message} Path: {candidate.Path}");
                    return SetRcloneStatus(ready);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await StopDaemonAsync().ConfigureAwait(false);
                    var msg = $"rclone at {candidate.Path} could not be started: {ex.Message}";
                    _log.Error("rclone", msg, ex);
                    return SetRcloneStatus(new RcloneStatus(RcloneState.StartFailed, candidate.Path, candidate.Source, line, 0, location.Searched, msg, DateTimeOffset.Now));
                }
            }

            await StopDaemonAsync().ConfigureAwait(false);
            return SetRcloneStatus(new RcloneStatus(RcloneState.Unusable, firstCandidate!.Path, firstCandidate.Source, null, 0, location.Searched,
                firstError + (existing.Count > 1 ? $" ({existing.Count} copies found, none usable.)" : string.Empty), DateTimeOffset.Now));
        }
        catch (OperationCanceledException)
        {
            SetRcloneStatus(_rcloneStatus with { State = RcloneState.NotChecked, Message = "rclone check was cancelled." });
            throw;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// Downloads the official rclone release (SHA-256 verified) into the CloudSBiian tools folder
    /// and re-runs the dependency check.
    /// </summary>
    public async Task<RcloneStatus> InstallRcloneAsync(IProgress<RcloneDownloadProgress>? progress, CancellationToken ct, Uri? baseUri = null)
    {
        var target = RcloneLocator.ManagedDirectory ?? throw new InvalidOperationException("No tools folder configured.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudSBiian/" + (typeof(AccountManager).Assembly.GetName().Version?.ToString(3) ?? "0"));
        var staged = await new RcloneInstaller(http, baseUri, _log)
            .DownloadAndVerifyAsync(Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar))!, progress, ct).ConfigureAwait(false);
        if (_daemonPath != null && string.Equals(Path.GetDirectoryName(_daemonPath), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            await ResetRcloneAsync().ConfigureAwait(false);
        }

        var exe = RcloneInstaller.Commit(staged, target);
        lock (_versionCache)
        {
            _versionCache.Clear();
        }

        _log.Info("rclone", $"Installed rclone {staged.Version} to {exe}.");
        progress?.Report(new RcloneDownloadProgress("Checking rclone", 0, null));
        return await CheckRcloneAsync(ct).ConfigureAwait(false);
    }

    private async Task<(Version? Version, string? Line, string? Error)> ValidateCachedAsync(string path, CancellationToken ct)
    {
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length;
        lock (_versionCache)
        {
            if (_versionCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var result = await RcloneLocator.ValidateAsync(path, ct).ConfigureAwait(false);
        lock (_versionCache)
        {
            _versionCache[key] = result;
        }

        return result;
    }

    private RcloneStatus SetRcloneStatus(RcloneStatus status)
    {
        _rcloneStatus = status;
        try
        {
            RcloneStatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _log.Debug("rclone", $"Status handler failed: {ex.Message}");
        }

        return status;
    }

    public (ImplementationStatus Status, string Note) EffectiveStatus(ProviderKind kind) =>
        ProviderRegistry.Effective(ProviderRegistry.Get(kind), _rcloneStatus.IsReady ? _catalog : null, _rcloneStatus);

    /// <summary>Connects every enabled auto-connect account in parallel. One failure never blocks the others.</summary>
    public async Task ConnectAllAsync(CancellationToken ct)
    {
        var tasks = Runtimes
            .Where(r => r.Account.Enabled && r.Account.AutoConnect)
            .Select(r => SafeConnectAsync(r.Id, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SafeConnectAsync(string id, CancellationToken ct)
    {
        try
        {
            await ConnectAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Already recorded in the runtime state.
        }
    }

    /// <summary>Adds a new account (a second, third... account of the same provider is just another entry).</summary>
    public async Task<AccountRuntime> AddAccountAsync(CloudAccount account, IReadOnlyDictionary<string, string> secrets, bool connect, CancellationToken ct)
    {
        AccountIdValidator.Validate(account.Id);
        if (_runtimes.ContainsKey(account.Id))
        {
            throw new InvalidOperationException("Account id already exists.");
        }

        if (secrets.Count > 0)
        {
            _secrets.SetMany(account.Id, secrets);
        }

        _store.Add(account);
        var runtime = new AccountRuntime(account);
        _runtimes[account.Id] = runtime;
        _log.Info("accounts", $"Account added: {account.DisplayName} ({account.Kind}).", account.Id);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        if (connect)
        {
            await SafeConnectAsync(account.Id, ct).ConfigureAwait(false);
        }

        return runtime;
    }

    public async Task ConnectAsync(string accountId, CancellationToken ct)
    {
        var r = Get(accountId) ?? throw new KeyNotFoundException(accountId);
        await r.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (r.IsConnected)
            {
                return;
            }

            SetState(r, AccountConnectionState.Connecting, null);
            ICloudStorageProvider? provider = null;
            try
            {
                provider = await CreateProviderAsync(r.Account, ct).ConfigureAwait(false);
                await provider.ConnectAsync(ct).ConfigureAwait(false);
                var test = await provider.TestConnectionAsync(ct).ConfigureAwait(false);
                r.LastTest = test;
                if (!test.Success)
                {
                    await DisposeProviderAsync(r.Account, provider).ConfigureAwait(false);
                    SetState(r, MapTest(test.Status), test.Message);
                    _log.Warn("accounts", $"Connect failed: {test.Message}", null, r.Id);
                    return;
                }

                r.Provider = provider;
                r.ConnectedAt = DateTimeOffset.Now;
                SetState(r, AccountConnectionState.Connected, null);
                _log.Info("accounts", $"Connected {r.Account.DisplayName}.", r.Id);
                _ = RefreshQuotaSafeAsync(r);
            }
            catch (CloudProviderException ex)
            {
                if (provider != null)
                {
                    await DisposeProviderAsync(r.Account, provider).ConfigureAwait(false);
                }

                SetState(r, MapError(ex.Kind), ex.Message);
                _log.Warn("accounts", "Connect failed.", ex, r.Id);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (provider != null)
                {
                    await DisposeProviderAsync(r.Account, provider).ConfigureAwait(false);
                }

                SetState(r, AccountConnectionState.Error, SecretRedactor.Redact(ex.Message));
                _log.Error("accounts", "Connect failed.", ex, r.Id);
                throw;
            }
        }
        finally
        {
            r.Gate.Release();
        }
    }

    public async Task DisconnectAsync(string accountId)
    {
        var r = Get(accountId);
        if (r == null)
        {
            return;
        }

        await r.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await PersistRefreshedSecretsAsync(r, CancellationToken.None).ConfigureAwait(false);
            if (r.Provider != null)
            {
                await DisposeProviderAsync(r.Account, r.Provider).ConfigureAwait(false);
            }

            r.Provider = null;
            r.ConnectedAt = null;
            SetState(r, AccountConnectionState.Disconnected, null);
            _log.Info("accounts", $"Disconnected {r.Account.DisplayName}.", r.Id);
        }
        finally
        {
            r.Gate.Release();
        }
    }

    /// <summary>
    /// Reconnects one account. For browser-OAuth accounts with <paramref name="reauthorize"/> the
    /// official sign-in page is opened again to obtain a fresh token for this account only.
    /// </summary>
    public async Task ReconnectAsync(string accountId, bool reauthorize, CancellationToken ct)
    {
        var r = Get(accountId) ?? throw new KeyNotFoundException(accountId);
        await DisconnectAsync(accountId).ConfigureAwait(false);
        if (reauthorize && r.Descriptor.Auth == ProviderAuthKind.OAuthBrowser && r.Account.Backend != null)
        {
            var token = await AuthorizeAsync(r.Account.Backend, r.Account.Options, ct).ConfigureAwait(false);
            _secrets.Set(r.Id, SecretToken, token);
        }

        await ConnectAsync(accountId, ct).ConfigureAwait(false);
    }

    public async Task<string> AuthorizeAsync(string backend, IReadOnlyDictionary<string, string>? options, CancellationToken ct)
    {
        var status = _rcloneStatus.IsReady ? _rcloneStatus : await CheckRcloneAsync(ct).ConfigureAwait(false);
        if (!status.IsReady || status.ExecutablePath == null)
        {
            throw new CloudProviderException(CloudErrorKind.DependencyMissing, status.BlockedNote);
        }

        return await new RcloneAuthorizer(status.ExecutablePath).AuthorizeAsync(backend, options, ct).ConfigureAwait(false);
    }

    public async Task<ConnectionTestResult> TestAsync(string accountId, CancellationToken ct)
    {
        var r = Get(accountId) ?? throw new KeyNotFoundException(accountId);
        ConnectionTestResult result;
        if (r.Provider != null)
        {
            result = await r.Provider.TestConnectionAsync(ct).ConfigureAwait(false);
        }
        else
        {
            ICloudStorageProvider? temp = null;
            try
            {
                temp = await CreateProviderAsync(r.Account, ct).ConfigureAwait(false);
                await temp.ConnectAsync(ct).ConfigureAwait(false);
                result = await temp.TestConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (CloudProviderException ex)
            {
                result = new ConnectionTestResult(MapErrorToTest(ex.Kind), ex.Message, TimeSpan.Zero);
            }
            finally
            {
                if (temp != null)
                {
                    await DisposeProviderAsync(r.Account, temp).ConfigureAwait(false);
                }
            }
        }

        r.LastTest = result;
        if (r.IsConnected && !result.Success)
        {
            SetState(r, MapTest(result.Status), result.Message);
        }
        else
        {
            RuntimeChanged?.Invoke(this, r);
        }

        return result;
    }

    public async Task RefreshAsync(string accountId, CancellationToken ct)
    {
        var r = Get(accountId) ?? throw new KeyNotFoundException(accountId);
        if (r.Provider == null)
        {
            return;
        }

        r.Quota = await r.Provider.GetQuotaAsync(ct).ConfigureAwait(false);
        if (r.Provider is IIdentityProvider ip)
        {
            var identity = await ip.GetIdentityAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(identity) && identity != r.Account.Identity)
            {
                r.Account.Identity = identity;
                _store.Update(r.Account);
            }
        }

        RuntimeChanged?.Invoke(this, r);
    }

    public void Rename(string accountId, string newName)
    {
        var r = Get(accountId) ?? throw new KeyNotFoundException(accountId);
        if (string.IsNullOrWhiteSpace(newName) || newName.Length > 80)
        {
            throw new ArgumentException("Name must be 1-80 characters.");
        }

        r.Account.DisplayName = newName.Trim();
        _store.Update(r.Account);
        RuntimeChanged?.Invoke(this, r);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateAccount(CloudAccount account)
    {
        _store.Update(account);
        if (_runtimes.TryGetValue(account.Id, out var r))
        {
            r.Account = account;
            RuntimeChanged?.Invoke(this, r);
        }
    }

    /// <summary>
    /// Removes the account from CloudSBiian only. Files on the cloud are never touched.
    /// Cache cleanup and transfer cancellation are done by the caller before this.
    /// </summary>
    public async Task RemoveAsync(string accountId, bool deleteCredentials)
    {
        await DisconnectAsync(accountId).ConfigureAwait(false);
        if (deleteCredentials)
        {
            _secrets.DeleteAll(accountId);
        }

        _store.Remove(accountId);
        _runtimes.TryRemove(accountId, out _);
        _log.Info("accounts", "Account removed from CloudSBiian (cloud data untouched).", accountId);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the cache/drive layers when an operation fails. Authentication failures move only
    /// this account to "expired" so the UI can offer Reconnect.
    /// </summary>
    public void ReportFailure(string accountId, Exception ex)
    {
        if (!_runtimes.TryGetValue(accountId, out var r) || ex is not CloudProviderException cpe)
        {
            return;
        }

        if (cpe.IsAuthentication && r.State == AccountConnectionState.Connected)
        {
            SetState(r, AccountConnectionState.AuthenticationExpired, cpe.Message);
        }
        else if (cpe.Kind == CloudErrorKind.Network && r.State == AccountConnectionState.Connected)
        {
            r.LastError = cpe.Message;
            RuntimeChanged?.Invoke(this, r);
        }
    }

    /// <summary>Re-checks every connected account after sleep/resume or network changes.</summary>
    public async Task RevalidateAllAsync(CancellationToken ct)
    {
        foreach (var r in Runtimes.Where(r => r.Account.Enabled))
        {
            try
            {
                if (r.IsConnected)
                {
                    await TestAsync(r.Id, ct).ConfigureAwait(false);
                }
                else if (r.State is AccountConnectionState.Offline or AccountConnectionState.Error && r.Account.AutoConnect)
                {
                    await SafeConnectAsync(r.Id, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("accounts", "Revalidation failed.", ex, r.Id);
            }
        }
    }

    /// <summary>Saves OAuth tokens that rclone refreshed in memory back to the DPAPI store.</summary>
    public async Task PersistAllRefreshedSecretsAsync(CancellationToken ct)
    {
        foreach (var r in Runtimes.Where(r => r.IsConnected))
        {
            await PersistRefreshedSecretsAsync(r, ct).ConfigureAwait(false);
        }
    }

    private async Task PersistRefreshedSecretsAsync(AccountRuntime r, CancellationToken ct)
    {
        if (r.Provider is not RcloneProvider || _daemon == null || !_daemon.IsRunning)
        {
            return;
        }

        try
        {
            var current = await new RcloneRemoteConfigurator(_daemon, _log).GetAsync(r.Id, ct).ConfigureAwait(false);
            if (current.TryGetValue(SecretToken, out var token) && !string.IsNullOrEmpty(token) && token != _secrets.Get(r.Id, SecretToken))
            {
                _secrets.Set(r.Id, SecretToken, token);
                _log.Debug("accounts", "Refreshed OAuth token persisted.", r.Id);
            }
        }
        catch (CloudProviderException ex)
        {
            _log.Debug("accounts", $"Token persistence skipped: {ex.Message}", r.Id);
        }
    }

    private async Task<ICloudStorageProvider> CreateProviderAsync(CloudAccount account, CancellationToken ct)
    {
        // Test hook: automated tests substitute in-memory backends for any provider kind.
        if (MockFactory?.Invoke(account) is { } mock)
        {
            return mock;
        }

        switch (account.Kind)
        {
            case ProviderKind.Mock:
                throw new CloudProviderException(CloudErrorKind.NotSupported, "Mock provider is only available in tests.");
            case ProviderKind.LocalFolder:
                if (!account.Options.TryGetValue(OptionPath, out var path) || string.IsNullOrWhiteSpace(path))
                {
                    throw new CloudProviderException(CloudErrorKind.InvalidPath, "No folder path configured.");
                }

                return new LocalFolderProvider(account.Id, path);
            case ProviderKind.WebDav when account.Backend == null:
                if (!account.Options.TryGetValue(OptionUrl, out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                {
                    throw new CloudProviderException(CloudErrorKind.InvalidPath, "Invalid WebDAV URL.");
                }

                account.Options.TryGetValue(OptionUser, out var user);
                return new WebDavProvider(account.Id, uri, user, _secrets.Get(account.Id, SecretPassword));
        }

        var backend = account.Backend ?? ProviderRegistry.Get(account.Kind).RcloneBackend
            ?? throw new CloudProviderException(CloudErrorKind.NotSupported, $"No backend for {account.Kind}.");
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false)
            ?? throw new CloudProviderException(CloudErrorKind.DependencyMissing, _rcloneStatus.BlockedNote);
        if (!catalog.Has(backend))
        {
            throw new CloudProviderException(CloudErrorKind.DependencyMissing,
                $"The configured rclone ({catalog.Version}) has no '{backend}' backend.");
        }

        var parameters = new Dictionary<string, string>(account.Options);
        foreach (var (k, v) in _secrets.GetAll(account.Id))
        {
            parameters[k] = v;
        }

        var configurator = new RcloneRemoteConfigurator(_daemon!, _log);
        await configurator.CreateAsync(account.Id, backend, parameters, ct).ConfigureAwait(false);
        return new RcloneProvider(account.Id, account.Kind, _daemon!, account.RootPath);
    }

    private async Task DisposeProviderAsync(CloudAccount account, ICloudStorageProvider provider)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            if (provider is RcloneProvider && _daemon != null)
            {
                await new RcloneRemoteConfigurator(_daemon, _log).DeleteAsync(account.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("accounts", $"Provider dispose: {ex.Message}", account.Id);
        }
    }

    private async Task<RcloneDaemon> GetDaemonAsync(string exe, CancellationToken ct)
    {
        await _rcloneGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_daemon != null && !string.Equals(_daemonPath, exe, StringComparison.OrdinalIgnoreCase))
            {
                _daemon.Exited -= OnDaemonExited;
                await _daemon.DisposeAsync().ConfigureAwait(false);
                _daemon = null;
                _catalog = null;
            }

            if (_daemon == null)
            {
                _daemon = new RcloneDaemon(exe, _log);
                _daemon.Exited += OnDaemonExited;
                _daemonPath = exe;
                _catalog = null;
            }

            await _daemon.EnsureStartedAsync(ct).ConfigureAwait(false);
            return _daemon;
        }
        finally
        {
            _rcloneGate.Release();
        }
    }

    private async Task StopDaemonAsync()
    {
        if (_daemon == null)
        {
            return;
        }

        await ResetRcloneAsync().ConfigureAwait(false);
    }

    private void OnDaemonExited(object? sender, EventArgs e)
    {
        // The in-memory remotes are gone: mark rclone accounts so the UI and auto-reconnect react.
        foreach (var r in _runtimes.Values.Where(r => r.Provider is RcloneProvider))
        {
            r.Provider = null;
            SetState(r, AccountConnectionState.Error, "rclone stopped unexpectedly. Use Reconnect.");
        }
    }

    /// <summary>Forgets the rclone daemon (e.g. after the rclone path changed in Settings).</summary>
    public async Task ResetRcloneAsync()
    {
        foreach (var r in _runtimes.Values.Where(r => r.Provider is RcloneProvider).ToList())
        {
            await DisconnectAsync(r.Id).ConfigureAwait(false);
        }

        await _rcloneGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_daemon != null)
            {
                _daemon.Exited -= OnDaemonExited;
                await _daemon.DisposeAsync().ConfigureAwait(false);
            }

            _daemon = null;
            _daemonPath = null;
            _catalog = null;
        }
        finally
        {
            _rcloneGate.Release();
        }

        if (_rcloneStatus.IsReady)
        {
            SetRcloneStatus(_rcloneStatus with { State = RcloneState.NotChecked, BackendCount = 0, Message = "rclone was stopped; it will be checked again." });
        }
    }

    private async Task RefreshQuotaSafeAsync(AccountRuntime r)
    {
        try
        {
            await RefreshAsync(r.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("accounts", $"Quota not available: {ex.Message}", r.Id);
        }
    }

    private void SetState(AccountRuntime r, AccountConnectionState state, string? error)
    {
        r.State = state;
        r.LastError = error == null ? null : SecretRedactor.Redact(error);
        RuntimeChanged?.Invoke(this, r);
    }

    private static AccountConnectionState MapTest(ConnectionTestStatus s) => s switch
    {
        ConnectionTestStatus.Ok => AccountConnectionState.Connected,
        ConnectionTestStatus.AuthenticationExpired => AccountConnectionState.AuthenticationExpired,
        ConnectionTestStatus.AuthenticationFailed => AccountConnectionState.AuthenticationFailed,
        ConnectionTestStatus.NetworkError => AccountConnectionState.Offline,
        ConnectionTestStatus.DependencyMissing => AccountConnectionState.Blocked,
        _ => AccountConnectionState.Error,
    };

    private static AccountConnectionState MapError(CloudErrorKind k) => k switch
    {
        CloudErrorKind.AuthenticationExpired => AccountConnectionState.AuthenticationExpired,
        CloudErrorKind.AuthenticationFailed or CloudErrorKind.PermissionDenied => AccountConnectionState.AuthenticationFailed,
        CloudErrorKind.Network or CloudErrorKind.Timeout => AccountConnectionState.Offline,
        CloudErrorKind.DependencyMissing => AccountConnectionState.Blocked,
        _ => AccountConnectionState.Error,
    };

    private static ConnectionTestStatus MapErrorToTest(CloudErrorKind k) => k switch
    {
        CloudErrorKind.AuthenticationExpired => ConnectionTestStatus.AuthenticationExpired,
        CloudErrorKind.AuthenticationFailed or CloudErrorKind.PermissionDenied => ConnectionTestStatus.AuthenticationFailed,
        CloudErrorKind.Network or CloudErrorKind.Timeout => ConnectionTestStatus.NetworkError,
        CloudErrorKind.RateLimited => ConnectionTestStatus.RateLimited,
        CloudErrorKind.DependencyMissing => ConnectionTestStatus.DependencyMissing,
        _ => ConnectionTestStatus.Error,
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await PersistAllRefreshedSecretsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        foreach (var r in _runtimes.Values)
        {
            if (r.Provider != null)
            {
                await DisposeProviderAsync(r.Account, r.Provider).ConfigureAwait(false);
                r.Provider = null;
            }
        }

        if (_daemon != null)
        {
            _daemon.Exited -= OnDaemonExited;
            await _daemon.DisposeAsync().ConfigureAwait(false);
        }
    }
}
