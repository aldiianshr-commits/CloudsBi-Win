using System.Text;
using CloudSBiian.CloudProviders;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.CloudProviders.WebDav;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Security;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.IntegrationTests;

/// <summary>
/// Real-protocol tests: CloudSBiian's rclone adapter and native WebDAV client against local rclone
/// servers (local folder, WebDAV, SFTP, S3). No cloud accounts are involved.
/// </summary>
[Collection("rclone")]
public class RcloneProviderTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private AppDatabase? _db;
    private AccountManager? _accounts;
    private InMemorySecretStore _secrets = new();

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "csb-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new AppDatabase(Path.Combine(_root, "t.db"));
        _accounts = new AccountManager(new AccountStore(_db), _secrets, () => null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_accounts != null)
        {
            await _accounts.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeData(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "folder", "sub"));
        File.WriteAllBytes(Path.Combine(dir, "folder", "data.bin"), Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello rclone");
        return dir;
    }

    private static async Task ExerciseProviderAsync(ICloudStorageProvider p)
    {
        var root = await p.ListAsync("/", default);
        Assert.Contains(root, e => e.Name == "hello.txt" && !e.IsDirectory && e.Size == 12);
        Assert.Contains(root, e => e.Name == "folder" && e.IsDirectory);

        var data = await p.StatAsync("/folder/data.bin", default);
        Assert.NotNull(data);
        Assert.Equal(300_000, data!.Size);
        Assert.Null(await p.StatAsync("/folder/missing.bin", default));

        await using (var s = await p.OpenReadAsync("/folder/data.bin", 1000, 500, default))
        {
            var buf = new byte[600];
            var n = await s.ReadFullAsync(buf, default);
            Assert.Equal(500, n);
            for (var i = 0; i < n; i++)
            {
                Assert.Equal((byte)((1000 + i) % 251), buf[i]);
            }
        }

        var payload = Encoding.UTF8.GetBytes("uploaded through CloudSBiian " + Guid.NewGuid());
        await p.UploadAsync("/folder/up.txt", new MemoryStream(payload), payload.Length, null, default);
        Assert.Equal(payload.Length, (await p.StatAsync("/folder/up.txt", default))!.Size);

        if (p is IFileUploadProvider fu)
        {
            var local = Path.GetTempFileName();
            await File.WriteAllBytesAsync(local, payload);
            long reported = 0;
            await fu.UploadFileAsync("/folder/up2.txt", local, new Progress<long>(b => reported = b), default);
            File.Delete(local);
            Assert.Equal(payload.Length, (await p.StatAsync("/folder/up2.txt", default))!.Size);
        }

        await p.MoveAsync("/folder/up.txt", "/folder/moved.txt", false, false, default);
        Assert.Null(await p.StatAsync("/folder/up.txt", default));
        Assert.NotNull(await p.StatAsync("/folder/moved.txt", default));

        await p.CreateDirectoryAsync("/newdir", default);
        Assert.True((await p.StatAsync("/newdir", default))!.IsDirectory);
        await p.MoveAsync("/newdir", "/renamed-dir", true, false, default);
        Assert.NotNull(await p.StatAsync("/renamed-dir", default));
        await p.DeleteDirectoryAsync("/renamed-dir", default);
        Assert.Null(await p.StatAsync("/renamed-dir", default));

        await p.DeleteFileAsync("/folder/moved.txt", default);
        Assert.Null(await p.StatAsync("/folder/moved.txt", default));
        await Assert.ThrowsAnyAsync<CloudProviderException>(() => p.DeleteDirectoryAsync("/folder", default));
    }

    [RcloneFact]
    public async Task Catalog_ListsOfficialBackends_AndTeraBoxStatusIsHonest()
    {
        var catalog = await _accounts!.GetCatalogAsync(default);
        Assert.NotNull(catalog);
        foreach (var b in new[] { "drive", "onedrive", "dropbox", "sftp", "s3", "webdav", "smb", "local" })
        {
            Assert.True(catalog!.Has(b), $"backend {b}");
        }

        var (status, _) = _accounts.EffectiveStatus(ProviderKind.TeraBox);
        Assert.Equal(catalog!.Has("terabox") ? ImplementationStatus.Experimental : ImplementationStatus.Blocked, status);
        Assert.Contains(catalog.Get("drive")!.Options, o => o.Name == "token" && o.IsSecret);
    }

    [RcloneFact]
    public async Task LocalBackend_ThroughAccountManager_SupportsAllOperations()
    {
        var dir = MakeData("local");
        var account = new CloudAccount
        {
            Id = CloudAccount.NewId(ProviderKind.GenericRclone),
            Kind = ProviderKind.GenericRclone,
            DisplayName = "Local via rclone",
            Backend = "local",
            RootPath = dir,
        };
        var runtime = await _accounts!.AddAccountAsync(account, new Dictionary<string, string>(), connect: true, default);
        Assert.Equal(AccountConnectionState.Connected, runtime.State);
        var provider = (RcloneProvider)runtime.Provider!;
        Assert.True(provider.Capabilities.HasFlag(ProviderCapabilities.RangeRead));
        Assert.True(await provider.ProbeRangeSupportAsync((await provider.StatAsync("/folder/data.bin", default))!, default));
        await ExerciseProviderAsync(provider);
        Assert.NotNull(await provider.GetQuotaAsync(default));
    }

    [RcloneFact]
    public async Task TwoAccounts_GetSeparateRemotes()
    {
        var a = MakeData("a");
        var b = MakeData("b");
        File.WriteAllText(Path.Combine(b, "only-b.txt"), "b");
        var ra = await _accounts!.AddAccountAsync(new CloudAccount { Id = "rclone_aaaaaa", Kind = ProviderKind.GenericRclone, DisplayName = "A", Backend = "local", RootPath = a }, new Dictionary<string, string>(), true, default);
        var rb = await _accounts.AddAccountAsync(new CloudAccount { Id = "rclone_bbbbbb", Kind = ProviderKind.GenericRclone, DisplayName = "B", Backend = "local", RootPath = b }, new Dictionary<string, string>(), true, default);
        Assert.DoesNotContain(await ra.Provider!.ListAsync("/", default), e => e.Name == "only-b.txt");
        Assert.Contains(await rb.Provider!.ListAsync("/", default), e => e.Name == "only-b.txt");

        await _accounts.DisconnectAsync("rclone_aaaaaa");
        Assert.Equal(AccountConnectionState.Connected, _accounts.Get("rclone_bbbbbb")!.State);
        Assert.Contains(await rb.Provider!.ListAsync("/", default), e => e.Name == "only-b.txt");
    }

    [RcloneFact]
    public async Task NativeWebDav_AgainstRcloneWebDavServer()
    {
        var dir = MakeData("webdav");
        using var server = await RcloneServer.StartAsync("webdav", dir, "--user", "alice", "--pass", "s3cret-pass");
        await using var provider = new WebDavProvider("webdav_test01", new Uri($"http://127.0.0.1:{server.Port}/"), "alice", "s3cret-pass");
        await provider.ConnectAsync(default);
        Assert.True((await provider.TestConnectionAsync(default)).Success);
        await ExerciseProviderAsync(provider);

        await using var wrong = new WebDavProvider("webdav_test02", new Uri($"http://127.0.0.1:{server.Port}/"), "alice", "wrong");
        var test = await wrong.TestConnectionAsync(default);
        Assert.Equal(ConnectionTestStatus.AuthenticationFailed, test.Status);
    }

    [RcloneFact]
    public async Task Sftp_ThroughRcloneBackend_WithPasswordSecret()
    {
        var dir = MakeData("sftp");
        using var server = await RcloneServer.StartAsync("sftp", dir, "--user", "bob", "--pass", "sftp-pass-123");
        var account = new CloudAccount
        {
            Id = CloudAccount.NewId(ProviderKind.Sftp),
            Kind = ProviderKind.Sftp,
            DisplayName = "SFTP test",
            Backend = "sftp",
            Options = new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1",
                ["port"] = server.Port.ToString(),
                ["user"] = "bob",
                ["shell_type"] = "none",
                ["disable_hashcheck"] = "true",
            },
        };
        var runtime = await _accounts!.AddAccountAsync(account, new Dictionary<string, string> { ["pass"] = "sftp-pass-123" }, true, default);
        Assert.True(runtime.State == AccountConnectionState.Connected, runtime.LastError);
        Assert.Equal("sftp-pass-123", _secrets.Get(account.Id, "pass"));
        Assert.Equal("***", SecretRedactor.Redact("sftp-pass-123"));
        await ExerciseProviderAsync(runtime.Provider!);
    }

    [RcloneFact]
    public async Task S3_ThroughRcloneBackend_AgainstRcloneS3Server()
    {
        var dir = Path.Combine(_root, "s3");
        Directory.CreateDirectory(dir);
        var bucket = MakeData(Path.Combine("s3", "bucket1"));
        using var server = await RcloneServer.StartAsync("s3", dir, "--auth-key", "TESTACCESSKEY,TESTSECRETKEY123");
        var account = new CloudAccount
        {
            Id = CloudAccount.NewId(ProviderKind.S3),
            Kind = ProviderKind.S3,
            DisplayName = "S3 test",
            Backend = "s3",
            RootPath = "/bucket1",
            Options = new Dictionary<string, string>
            {
                ["provider"] = "Other",
                ["endpoint"] = $"http://127.0.0.1:{server.Port}",
                ["access_key_id"] = "TESTACCESSKEY",
                ["force_path_style"] = "true",
            },
        };
        var runtime = await _accounts!.AddAccountAsync(account, new Dictionary<string, string> { ["secret_access_key"] = "TESTSECRETKEY123" }, true, default);
        Assert.True(runtime.State == AccountConnectionState.Connected, runtime.LastError);
        var p = runtime.Provider!;
        var list = await p.ListAsync("/", default);
        Assert.Contains(list, e => e.Name == "hello.txt");
        await using (var s = await p.OpenReadAsync("/folder/data.bin", 1000, 10, default))
        {
            var buf = new byte[10];
            Assert.Equal(10, await s.ReadFullAsync(buf, default));
            Assert.Equal((byte)(1000 % 251), buf[0]);
        }

        var payload = Encoding.UTF8.GetBytes("s3 upload");
        await p.UploadAsync("/folder/s3.txt", new MemoryStream(payload), payload.Length, null, default);
        Assert.Equal(payload.Length, (await p.StatAsync("/folder/s3.txt", default))!.Size);
        await p.DeleteFileAsync("/folder/s3.txt", default);
        Assert.Null(await p.StatAsync("/folder/s3.txt", default));
    }
}
