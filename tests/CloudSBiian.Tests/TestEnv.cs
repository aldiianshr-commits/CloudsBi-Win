using System.Collections.Concurrent;
using CloudSBiian.CacheEngine;
using CloudSBiian.CloudProviders;
using CloudSBiian.CloudProviders.Mock;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Security;
using CloudSBiian.Core.Storage;
using CloudSBiian.Monitoring;
using CloudSBiian.PredictionEngine;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.Tests;

/// <summary>
/// Full service graph over a temporary directory, with in-memory providers substituted for the
/// cloud backends (results are "mock" results, see TEST_REPORT.md).
/// </summary>
public sealed class TestEnv : IAsyncDisposable
{
    private static readonly Lazy<Core.Logging.AppLog> SharedLog = new(() =>
        new Core.Logging.AppLog(Path.Combine(AppContext.BaseDirectory, "test-logs"), Core.Logging.LogLevel.Debug));

    private TestEnv(string root, AppSettings settings)
    {
        Root = root;
        Settings = settings;
        Log = SharedLog.Value;
        Db = new AppDatabase(Path.Combine(root, "test.db"));
        Metrics = new MetricsRegistry();
        Transfers = new TransferTracker(Metrics);
        Secrets = new InMemorySecretStore();
        Store = new AccountStore(Db);
        Accounts = new AccountManager(Store, Secrets, () => null, Log)
        {
            MockFactory = a => Providers.TryGetValue(a.Id, out var p) ? p : null,
        };
        Cache = new CacheService(Path.Combine(root, "cache"), Db, Accounts, Metrics, Transfers, () => Settings, Log);
        Staging = new StagingManager(Cache.StagingRoot);
        Uploads = new UploadQueue(Db, Staging, Accounts, Transfers, Cache.Metadata, Cache.Store, () => Settings.MaxUploadAttempts, Log);
        Profiles = new AccessProfileStore(Db);
    }

    public string Root { get; }

    public Core.Logging.IAppLog Log { get; }

    public AppSettings Settings { get; }

    public AppDatabase Db { get; }

    public MetricsRegistry Metrics { get; }

    public TransferTracker Transfers { get; }

    public InMemorySecretStore Secrets { get; }

    public AccountStore Store { get; }

    public AccountManager Accounts { get; }

    public ConcurrentDictionary<string, InMemoryProvider> Providers { get; } = new();

    public CacheService Cache { get; }

    public StagingManager Staging { get; }

    public UploadQueue Uploads { get; }

    public AccessProfileStore Profiles { get; }

    public PredictionService? Prediction { get; private set; }

    public static TestEnv Create(Action<AppSettings>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "csb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new AppSettings
        {
            ChunkSizeMiB = 1,
            CacheBudgetBytes = 64L * 1024 * 1024,
            MinFreeDiskBytes = 0,
            PrefetchBandwidthLimitBytesPerSecond = 0,
            MetadataTtlSeconds = 30,
        };
        configure?.Invoke(settings);
        return new TestEnv(root, settings);
    }

    public PredictionService EnablePrediction()
    {
        Prediction ??= new PredictionService(Cache, Profiles, Accounts, () => Settings);
        return Prediction;
    }

    public DriveServices DriveServices(PredictionService? prediction = null) => new()
    {
        Accounts = Accounts,
        Cache = Cache,
        Staging = Staging,
        Uploads = Uploads,
        Prediction = prediction,
        Settings = () => Settings,
        Log = Log,
    };

    public async Task<(string Id, InMemoryProvider Provider)> AddAccountAsync(string name, ProviderKind kind = ProviderKind.Mock, bool supportsRange = true, IReadOnlyDictionary<string, string>? secrets = null)
    {
        var account = new CloudAccount { Id = CloudAccount.NewId(kind), Kind = kind, DisplayName = name };
        var provider = new InMemoryProvider(account.Id, supportsRange);
        Providers[account.Id] = provider;
        await Accounts.AddAccountAsync(account, secrets ?? new Dictionary<string, string>(), connect: true, CancellationToken.None);
        return (account.Id, provider);
    }

    public static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Prediction?.Dispose();
        Uploads.Dispose();
        Cache.Dispose();
        await Accounts.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
