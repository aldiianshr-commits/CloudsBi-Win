using CloudSBiian.CloudProviders;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;

namespace CloudSBiian.Tests;

/// <summary>Multi-account behaviour (TeraBox kind, backed by in-memory providers).</summary>
public class AccountTests
{
    [Fact]
    public async Task AddingSeveralTeraBoxAccounts_GivesIndependentEntries()
    {
        await using var env = TestEnv.Create();
        var (a, _) = await env.AddAccountAsync("TeraBox 1", ProviderKind.TeraBox, secrets: new Dictionary<string, string> { ["cookie"] = "cookie-A" });
        var (b, _) = await env.AddAccountAsync("TeraBox 2", ProviderKind.TeraBox, secrets: new Dictionary<string, string> { ["cookie"] = "cookie-B" });
        var (c, _) = await env.AddAccountAsync("TeraBox 3", ProviderKind.TeraBox);

        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.All(env.Accounts.Runtimes, r => Assert.Equal(AccountConnectionState.Connected, r.State));
        Assert.Equal("cookie-A", env.Secrets.Get(a, "cookie"));
        Assert.Equal("cookie-B", env.Secrets.Get(b, "cookie"));
        Assert.Null(env.Secrets.Get(c, "cookie"));
        Assert.NotSame(env.Accounts.TryGetProvider(a), env.Accounts.TryGetProvider(b));
    }

    [Fact]
    public async Task SuggestDisplayName_SkipsUsedNames()
    {
        await using var env = TestEnv.Create();
        await env.AddAccountAsync("TeraBox", ProviderKind.TeraBox);
        await env.AddAccountAsync("TeraBox 2", ProviderKind.TeraBox);
        Assert.Equal("TeraBox 3", env.Store.SuggestDisplayName("TeraBox"));
    }

    [Fact]
    public async Task SessionExpiry_AffectsOnlyThatAccount()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A", ProviderKind.TeraBox);
        var (b, _) = await env.AddAccountAsync("B", ProviderKind.TeraBox);

        pa.SessionExpired = true;
        env.Accounts.ReportFailure(a, new CloudProviderException(CloudErrorKind.AuthenticationExpired, "expired"));

        Assert.Equal(AccountConnectionState.AuthenticationExpired, env.Accounts.Get(a)!.State);
        Assert.Equal(AccountConnectionState.Connected, env.Accounts.Get(b)!.State);
        Assert.NotNull(env.Accounts.TryGetProvider(b));

        pa.SessionExpired = false;
        await env.Accounts.ReconnectAsync(a, reauthorize: false, CancellationToken.None);
        Assert.Equal(AccountConnectionState.Connected, env.Accounts.Get(a)!.State);
    }

    [Fact]
    public async Task FailedConnect_DoesNotBlockOtherAccounts()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A");
        var (b, pb) = await env.AddAccountAsync("B");
        await env.Accounts.DisconnectAsync(a);
        await env.Accounts.DisconnectAsync(b);

        pa.FailNext(10, CloudErrorKind.AuthenticationFailed);
        await env.Accounts.ConnectAllAsync(CancellationToken.None);

        Assert.NotEqual(AccountConnectionState.Connected, env.Accounts.Get(a)!.State);
        Assert.Equal(AccountConnectionState.Connected, env.Accounts.Get(b)!.State);
        Assert.NotNull(env.Accounts.Get(a)!.LastError);
    }

    [Fact]
    public async Task TestConnection_ReportsRateLimitAndRecovers()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A");
        pa.FailNext(1, CloudErrorKind.RateLimited);
        var limited = await env.Accounts.TestAsync(a, CancellationToken.None);
        Assert.Equal(ConnectionTestStatus.RateLimited, limited.Status);
        var ok = await env.Accounts.TestAsync(a, CancellationToken.None);
        Assert.True(ok.Success);
    }

    [Fact]
    public async Task RemovingOneAccount_KeepsTheOthersAndTheirCache()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A", ProviderKind.TeraBox, secrets: new Dictionary<string, string> { ["cookie"] = "x-A" });
        var (b, pb) = await env.AddAccountAsync("B", ProviderKind.TeraBox, secrets: new Dictionary<string, string> { ["cookie"] = "x-B" });
        pa.AddFile("/f.bin", TestEnv.RandomBytes(300_000, 1));
        pb.AddFile("/f.bin", TestEnv.RandomBytes(300_000, 2));
        var ea = (await pa.StatAsync("/f.bin", default))!;
        var eb = (await pb.StatAsync("/f.bin", default))!;
        var buf = new byte[1000];
        await env.Cache.ReadAsync(a, ea, 0, buf, FetchPriority.P0, default);
        await env.Cache.ReadAsync(b, eb, 0, buf, FetchPriority.P0, default);

        env.Cache.ClearAccount(a);
        await env.Accounts.RemoveAsync(a, deleteCredentials: true);

        Assert.Null(env.Accounts.Get(a));
        Assert.Null(env.Secrets.Get(a, "cookie"));
        Assert.Equal("x-B", env.Secrets.Get(b, "cookie"));
        Assert.Equal(0, env.Cache.Store.PhysicalBytes(a));
        Assert.True(env.Cache.Store.PhysicalBytes(b) > 0);
        Assert.Equal(AccountConnectionState.Connected, env.Accounts.Get(b)!.State);
        Assert.True(pa.Exists("/f.bin"), "Removing an account must never delete cloud files.");
    }

    [Fact]
    public async Task AccountList_SurvivesRestart()
    {
        await using var env = TestEnv.Create();
        await env.AddAccountAsync("First", ProviderKind.TeraBox);
        await env.AddAccountAsync("Second", ProviderKind.TeraBox);
        env.Accounts.Rename(env.Accounts.Runtimes[0].Id, "Renamed");

        var restarted = new AccountManager(new AccountStore(env.Db), env.Secrets, () => null);
        var names = restarted.Runtimes.Select(r => r.Account.DisplayName).ToList();
        Assert.Equal(new[] { "Renamed", "Second" }, names);
        Assert.All(restarted.Runtimes, r => Assert.Equal(AccountConnectionState.Disconnected, r.State));
    }

    [Fact]
    public async Task ConcurrentTransfersOnTwoAccounts_DoNotInterfere()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A", ProviderKind.TeraBox);
        var (b, pb) = await env.AddAccountAsync("B", ProviderKind.TeraBox);
        var da = TestEnv.RandomBytes(3_000_000, 10);
        var db = TestEnv.RandomBytes(3_000_000, 20);
        pa.AddFile("/same/name.bin", da);
        pb.AddFile("/same/name.bin", db);
        pa.Latency = TimeSpan.FromMilliseconds(5);
        pb.Latency = TimeSpan.FromMilliseconds(5);
        var ea = (await pa.StatAsync("/same/name.bin", default))!;
        var eb = (await pb.StatAsync("/same/name.bin", default))!;

        var ra = new byte[da.Length];
        var rb = new byte[db.Length];
        await Task.WhenAll(
            env.Cache.ReadAsync(a, ea, 0, ra, FetchPriority.P0, default),
            env.Cache.ReadAsync(b, eb, 0, rb, FetchPriority.P0, default));

        Assert.Equal(da, ra);
        Assert.Equal(db, rb);
        Assert.NotEqual(env.Cache.Store.GetFile(a, "/same/name.bin")!.FileKey, env.Cache.Store.GetFile(b, "/same/name.bin")!.FileKey);
    }
}
