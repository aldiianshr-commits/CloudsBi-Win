using System.Net.NetworkInformation;
using CloudSBiian.CacheEngine;
using CloudSBiian.CloudProviders;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Security;
using CloudSBiian.Core.Storage;
using CloudSBiian.GameLauncher;
using CloudSBiian.Monitoring;
using CloudSBiian.PredictionEngine;
using CloudSBiian.VirtualDrive;
using Microsoft.Win32;

namespace CloudSBiian.Desktop.Services;

/// <summary>
/// Composition root: creates every service once, wires lifecycle events (sleep/resume, network
/// changes) and owns orderly shutdown.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly AppLog _fileLog;
    private readonly Timer _housekeeping;
    private readonly CancellationTokenSource _shutdown = new();

    public AppHost(AppPaths paths)
    {
        Paths = paths;
        Settings = new SettingsStore(paths.SettingsPath);
        _fileLog = new AppLog(paths.LogDirectory, Enum.TryParse<LogLevel>(Settings.Current.LogLevel, out var lvl) ? lvl : LogLevel.Info);
        Database = new AppDatabase(paths.DatabasePath);
        ActivityLog = new ActivityLog(Database, _fileLog);
        Log = ActivityLog;
        Metrics = new MetricsRegistry();
        Transfers = new TransferTracker(Metrics);
        Sampler = new MetricsSampler(Metrics, Transfers);
        Secrets = new DpapiSecretStore(paths.SecretsDirectory);
        AccountStore = new AccountStore(Database);
        CloudProviders.Rclone.RcloneLocator.ManagedDirectory = Path.Combine(paths.ToolsDirectory, "rclone");
        Accounts = new AccountManager(AccountStore, Secrets, () => Settings.Current.RclonePath, Log);

        var cacheDir = string.IsNullOrWhiteSpace(Settings.Current.CacheDirectory) ? paths.DefaultCacheDirectory : Settings.Current.CacheDirectory!;
        Cache = new CacheService(cacheDir, Database, Accounts, Metrics, Transfers, () => Settings.Current, Log);
        Staging = new StagingManager(Cache.StagingRoot);
        Uploads = new UploadQueue(Database, Staging, Accounts, Transfers, Cache.Metadata, Cache.Store, () => Settings.Current.MaxUploadAttempts, Log);
        Profiles = new AccessProfileStore(Database);
        Prediction = new PredictionService(Cache, Profiles, Accounts, () => Settings.Current, Log);
        DriveServices = new DriveServices
        {
            Accounts = Accounts,
            Cache = Cache,
            Staging = Staging,
            Uploads = Uploads,
            Prediction = Prediction,
            Settings = () => Settings.Current,
            Log = Log,
        };
        Drives = new DriveManager(DriveServices);
        Library = new LibraryStore(Database);
        Launcher = new LaunchService(Library, (a, p) => Drives.ResolveWindowsPath(a, p), Prediction, Log);
        Prepare = new PrepareService(Accounts, Cache, Uploads, Profiles, Transfers, () => Settings.Current, Launcher, Log);

        _fileLog.EntryWritten += (_, e) => LogWritten?.Invoke(this, e);
        Settings.Changed += (_, s) => _fileLog.MinimumLevel = Enum.TryParse<LogLevel>(s.LogLevel, out var l) ? l : LogLevel.Info;
        Cache.Warning += (_, message) => Log.Warn("cache", message);
        _housekeeping = new Timer(_ => Housekeeping(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public AppPaths Paths { get; }

    public SettingsStore Settings { get; }

    public IAppLog Log { get; }

    public AppLog FileLog => _fileLog;

    public ActivityLog ActivityLog { get; }

    public AppDatabase Database { get; }

    public MetricsRegistry Metrics { get; }

    public TransferTracker Transfers { get; }

    public MetricsSampler Sampler { get; }

    public ISecretStore Secrets { get; }

    public AccountStore AccountStore { get; }

    public AccountManager Accounts { get; }

    public CacheService Cache { get; }

    public StagingManager Staging { get; }

    public UploadQueue Uploads { get; }

    public AccessProfileStore Profiles { get; }

    public PredictionService Prediction { get; }

    public DriveServices DriveServices { get; }

    public DriveManager Drives { get; }

    public LibraryStore Library { get; }

    public LaunchService Launcher { get; }

    public PrepareService Prepare { get; }

    public event EventHandler<LogEntry>? LogWritten;

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Startup sequence. Each step is isolated so one failure does not stop the others.</summary>
    public async Task StartAsync()
    {
        Log.Info("app", $"CloudSBiian {typeof(AppHost).Assembly.GetName().Version} starting (data: {Paths.DataRoot}{(Paths.IsPortable ? ", portable" : string.Empty)}).");
        RecoverUnqueuedStaging();
        Uploads.Start();
        Launcher.RecoverAfterRestart();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        var rclone = await Accounts.CheckRcloneAsync(_shutdown.Token);
        if (!rclone.IsReady)
        {
            Log.Warn("rclone", $"rclone-based providers are blocked: {rclone.Message}");
        }

        await Accounts.ConnectAllAsync(_shutdown.Token);

        foreach (var mount in Settings.Current.Mounts.Where(m => m.MountOnStartup))
        {
            try
            {
                Drives.Mount(mount);
            }
            catch (Exception ex)
            {
                Log.Warn("drive", $"Automatic mount of {mount.DriveLetter}: failed.", ex);
            }
        }
    }

    /// <summary>
    /// Crash recovery: data written to the staging area that never made it into the queue is queued
    /// again (it is user data, so it must not be silently dropped).
    /// </summary>
    private void RecoverUnqueuedStaging()
    {
        try
        {
            var orphans = Staging.FindUnqueued((a, p) => Uploads.Get(a, p) != null);
            foreach (var (meta, data) in orphans)
            {
                if (AccountStore.Get(meta.AccountId) == null)
                {
                    continue;
                }

                Uploads.Enqueue(meta.AccountId, meta.Path, data, new FileInfo(data).Length, meta.BaseIdentity);
                Log.Warn("uploads", $"Recovered unsaved changes for {meta.Path} after an unexpected shutdown; queued for upload.", null, meta.AccountId);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("uploads", "Staging recovery failed.", ex);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Log.Info("app", "System is suspending.");
            _ = Accounts.PersistAllRefreshedSecretsAsync(CancellationToken.None);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Log.Info("app", "System resumed: revalidating connections and refreshing metadata.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                Cache.Metadata.InvalidateAll();
                await Accounts.RevalidateAllAsync(_shutdown.Token);
                Uploads.RetryAll();
            });
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Log.Info("app", e.IsAvailable ? "Network available." : "Network unavailable.");
        if (e.IsAvailable)
        {
            _ = Task.Run(async () =>
            {
                await Accounts.RevalidateAllAsync(_shutdown.Token);
                Uploads.RetryAll();
            });
        }
    }

    private void Housekeeping()
    {
        try
        {
            _ = Accounts.PersistAllRefreshedSecretsAsync(_shutdown.Token);
            ActivityLog.Prune(TimeSpan.FromDays(30));
        }
        catch (Exception ex)
        {
            Log.Debug("app", $"Housekeeping: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes an account from CloudSBiian. Cloud files are never deleted. Pending uploads must be
    /// discarded explicitly (<paramref name="discardPendingUploads"/>) or the removal is refused.
    /// </summary>
    public async Task RemoveAccountAsync(string accountId, bool deleteCredentials, bool discardPendingUploads)
    {
        var pending = Uploads.GetAll(accountId);
        if (pending.Count > 0 && !discardPendingUploads)
        {
            throw new InvalidOperationException($"This account still has {pending.Count} change(s) that were not uploaded. Upload them first or choose to discard them.");
        }

        Uploads.StopAccount(accountId);
        if (discardPendingUploads)
        {
            Uploads.DiscardAccount(accountId);
        }

        // Drives that expose only this account are unmounted; shared drives just lose the folder.
        foreach (var d in Drives.Mounted.Where(d => d.Settings.AccountIds.Count == 1 && d.Settings.AccountIds[0] == accountId).ToList())
        {
            Drives.Unmount(d.Letter);
        }

        var settings = Settings.Current.Clone();
        foreach (var m in settings.Mounts)
        {
            m.AccountIds.Remove(accountId);
        }

        Settings.Save(settings);
        Cache.ClearAccount(accountId);
        Profiles.Clear(accountId);
        try
        {
            Staging.DeleteAccount(accountId);
        }
        catch (IOException ex)
        {
            Log.Warn("accounts", "Some staging files could not be deleted.", ex, accountId);
        }

        Library.RemoveForAccount(accountId);
        await Accounts.RemoveAsync(accountId, deleteCredentials);
    }

    /// <summary>Runs the rclone dependency check again and connects accounts that were waiting for it.</summary>
    public async Task<CloudProviders.Rclone.RcloneStatus> RecheckRcloneAsync()
    {
        var status = await Accounts.CheckRcloneAsync(_shutdown.Token);
        if (status.IsReady)
        {
            await Accounts.ConnectAllAsync(_shutdown.Token);
        }

        return status;
    }

    /// <summary>Downloads the official rclone into the tools folder, checks it and connects waiting accounts.</summary>
    public async Task<CloudProviders.Rclone.RcloneStatus> InstallRcloneAsync(IProgress<CloudProviders.Rclone.RcloneDownloadProgress>? progress, CancellationToken ct)
    {
        var status = await Accounts.InstallRcloneAsync(progress, ct);
        if (status.IsReady)
        {
            await Accounts.ConnectAllAsync(_shutdown.Token);
        }

        return status;
    }

    /// <summary>Stores a user-selected (already validated) rclone.exe in Settings and switches to it.</summary>
    public async Task UseRcloneExecutableAsync(string path)
    {
        var previous = Settings.Current.Clone();
        if (string.Equals(previous.RclonePath, path, StringComparison.OrdinalIgnoreCase))
        {
            await Accounts.ResetRcloneAsync();
            await RecheckRcloneAsync();
            return;
        }

        var updated = Settings.Current.Clone();
        updated.RclonePath = path;
        await ApplySettingsAsync(updated, previous);
    }

    /// <summary>Applies changed settings that need live reconfiguration.</summary>
    public async Task ApplySettingsAsync(AppSettings updated, AppSettings previous)
    {
        Settings.Save(updated);
        if (!string.Equals(updated.RclonePath ?? string.Empty, previous.RclonePath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            await Accounts.ResetRcloneAsync();
            await RecheckRcloneAsync();
        }

        if (updated.CacheBudgetBytes < previous.CacheBudgetBytes)
        {
            Cache.EnsureSpace(0);
        }

        if (!updated.PredictionEnabled || updated.PredictionAggressiveness == PredictionAggressiveness.Off)
        {
            Prediction.ClearQueue();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info("app", "Shutting down.");
        _shutdown.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _housekeeping.Dispose();
        Drives.Dispose();
        Prediction.Dispose();
        Uploads.Dispose();
        Cache.Dispose();
        Sampler.Dispose();
        await Accounts.DisposeAsync();
        _fileLog.Dispose();
    }
}
