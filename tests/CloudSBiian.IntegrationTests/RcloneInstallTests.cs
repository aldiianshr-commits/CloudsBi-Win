using CloudSBiian.CloudProviders;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Security;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.IntegrationTests;

/// <summary>Runs when downloads.rclone.org may be used (set CSB_OFFLINE=1 to skip).</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CSB_OFFLINE") == "1")
        {
            Skip = "Network tests disabled (CSB_OFFLINE=1).";
        }
    }
}

/// <summary>
/// Real "Download rclone" flow: official release from downloads.rclone.org, SHA-256 checked
/// against SHA256SUMS, installed into a CloudSBiian tools folder, then used for real file
/// operations. Also checks the fallback when the path in Settings is wrong.
/// </summary>
[Collection("rclone")]
public sealed class RcloneInstallTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csb-rclone-install-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousManaged = RcloneLocator.ManagedDirectory;
    private AppDatabase? _db;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _db = new AppDatabase(Path.Combine(_root, "t.db"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        RcloneLocator.ManagedDirectory = _previousManaged;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(_root, true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }

        return Task.CompletedTask;
    }

    [NetworkFact]
    public async Task DownloadRclone_VerifiesInstallsAndUnblocksProviders()
    {
        var managed = Path.Combine(_root, "tools", "rclone");
        RcloneLocator.ManagedDirectory = managed;
        // Only the bundled/managed locations: proves the downloaded copy is the one being used even
        // though the CI machine also has rclone on PATH.
        Func<string?, RcloneLocation> onlyManaged = s =>
        {
            var all = RcloneLocator.Locate(s);
            var mine = all.Searched.Where(c => c.Source == "downloaded by CloudSBiian").ToList();
            var hit = mine.FirstOrDefault(c => c.Exists);
            return new RcloneLocation(hit?.Path, hit?.Source, mine, null);
        };

        await using var am = new AccountManager(new AccountStore(_db!), new InMemorySecretStore(), () => null) { RcloneLocate = onlyManaged };
        Assert.Equal(RcloneState.Missing, (await am.CheckRcloneAsync(default)).State);
        Assert.Equal(ImplementationStatus.Blocked, am.EffectiveStatus(ProviderKind.GoogleDrive).Status);

        var stages = new List<RcloneDownloadProgress>();
        var status = await am.InstallRcloneAsync(new SyncProgress<RcloneDownloadProgress>(stages.Add), default);

        Assert.True(status.IsReady, status.Message);
        Assert.Equal("downloaded by CloudSBiian", status.Source);
        Assert.Equal(Path.Combine(managed, "rclone.exe"), status.ExecutablePath);
        Assert.StartsWith("v1.", status.Version);
        Assert.True(File.Exists(Path.Combine(managed, "SOURCE.txt")));
        Assert.Contains(stages, p => p.Stage == "Downloading rclone" && p.BytesDone > 1_000_000);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "tools"), "rclone-download-*"));

        // Providers are now judged by the real backend list of the downloaded rclone.
        Assert.Equal(ImplementationStatus.Experimental, am.EffectiveStatus(ProviderKind.GoogleDrive).Status);
        Assert.Equal(ImplementationStatus.Experimental, am.EffectiveStatus(ProviderKind.Sftp).Status);
        Assert.Equal(am.Catalog!.Has("terabox") ? ImplementationStatus.Experimental : ImplementationStatus.Blocked, am.EffectiveStatus(ProviderKind.TeraBox).Status);

        // And it performs real file operations.
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "a.txt"), "downloaded rclone works");
        var account = new CloudAccount
        {
            Id = CloudAccount.NewId(ProviderKind.GenericRclone),
            Kind = ProviderKind.GenericRclone,
            DisplayName = "Local via downloaded rclone",
            Backend = "local",
            RootPath = data,
        };
        var runtime = await am.AddAccountAsync(account, new Dictionary<string, string>(), connect: true, default);
        Assert.Equal(AccountConnectionState.Connected, runtime.State);
        var list = await runtime.Provider!.ListAsync("/", default);
        Assert.Contains(list, e => e.Name == "a.txt" && e.Size == 23);
        await runtime.Provider.UploadAsync("/b.txt", new MemoryStream("xyz"u8.ToArray()), 3, null, default);
        Assert.True(File.Exists(Path.Combine(data, "b.txt")));
    }

    [RcloneFact]
    public async Task WrongSettingsPath_FallsBackToInstalledRclone_AndSaysSo()
    {
        var missing = Path.Combine(_root, "does-not-exist", "rclone.exe");
        await using var am = new AccountManager(new AccountStore(_db!), new InMemorySecretStore(), () => missing);

        var status = await am.CheckRcloneAsync(default);

        Assert.True(status.IsReady, status.Message);
        Assert.NotEqual(RcloneLocator.SettingsSource, status.Source);
        Assert.Contains("does not exist", status.Message);
        Assert.Contains(status.Searched, c => c.Path == missing && !c.Exists);
        Assert.True(status.BackendCount > 30);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            lock (this)
            {
                report(value);
            }
        }
    }
}
