using System.Diagnostics;
using System.Security.Cryptography;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Tests;
using CloudSBiian.VirtualDrive;
using Xunit.Abstractions;

namespace CloudSBiian.IntegrationTests;

/// <summary>
/// End-to-end tests through the real WinFsp driver: Windows APIs (System.IO) against a mounted
/// CloudSBiian drive backed by in-memory providers.
/// </summary>
[Collection("winfsp")]
public class WinFspDriveTests
{
    private const int MiB = 1024 * 1024;
    private readonly ITestOutputHelper _out;

    public WinFspDriveTests(ITestOutputHelper output) => _out = output;

    private static string FreeLetter() =>
        DriveManager.AvailableLetters().LastOrDefault(l => l is not ("X" or "Y" or "Z")) ?? DriveManager.AvailableLetters().Last();

    private static async Task<(TestEnv Env, DriveManager Drives, MountedDrive Drive)> MountAsync(Action<TestEnv>? setup = null, bool readOnly = false, bool allAccounts = false)
    {
        var env = TestEnv.Create();
        setup?.Invoke(env);
        if (env.Accounts.Runtimes.Count == 0)
        {
            await env.AddAccountAsync("Main");
        }

        env.Uploads.Start();
        var drives = new DriveManager(env.DriveServices());
        var settings = new DriveMountSettings
        {
            DriveLetter = FreeLetter(),
            AccountIds = allAccounts ? new List<string>() : new List<string> { env.Accounts.Runtimes[0].Id },
            ReadOnly = readOnly,
        };
        var drive = drives.Mount(settings);
        return (env, drives, drive);
    }

    [WinFspFact]
    public async Task Mount_Browse_ReadSequentialAndRandom_Unmount()
    {
        var data = TestEnv.RandomBytes(6 * MiB + 321, 1);
        var (env, drives, drive) = await MountAsync();
        await using var envScope = env;
        var p = env.Providers.Values.Single();
        p.AddFile("/Games/Demo/data.pak", data);
        p.AddFile("/docs/readme.txt", "hello from the cloud"u8.ToArray());

        Assert.Contains(Path.Combine(drive.Root, "Games"), Directory.GetDirectories(drive.Root));
        Assert.Equal("hello from the cloud", File.ReadAllText(Path.Combine(drive.Root, "docs", "readme.txt")));
        var path = Path.Combine(drive.Root, "Games", "Demo", "data.pak");
        Assert.Equal(data.Length, new FileInfo(path).Length);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(path)));

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
        {
            var rnd = new Random(7);
            for (var i = 0; i < 100; i++)
            {
                var offset = rnd.NextInt64(0, data.Length - 70_000);
                var buf = new byte[rnd.Next(1, 65_536)];
                fs.Seek(offset, SeekOrigin.Begin);
                var n = fs.Read(buf, 0, buf.Length);
                Assert.True(buf.AsSpan(0, n).SequenceEqual(data.AsSpan((int)offset, n)));
            }
        }

        drives.Unmount(drive.Letter);
        Assert.False(Directory.Exists(drive.Root));
    }

    [WinFspFact]
    public async Task ConcurrentReaders_GetConsistentData()
    {
        var data = TestEnv.RandomBytes(8 * MiB, 2);
        var (env, drives, drive) = await MountAsync();
        await using var envScope = env;
        env.Providers.Values.Single().AddFile("/big.bin", data);
        var path = Path.Combine(drive.Root, "big.bin");
        var expected = SHA256.HashData(data);
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), async (_, ct) =>
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            Assert.Equal(expected, SHA256.HashData(bytes));
        });
        await TestEnv.WaitUntilAsync(() => drive.Core.OpenHandles == 0, TimeSpan.FromSeconds(10), "handles released");
        drives.Dispose();
    }

    [WinFspFact]
    public async Task ExplorerStyleWrites_ReachTheCloud()
    {
        var (env, drives, drive) = await MountAsync(e => { });
        await using var envScope = env;
        var p = env.Providers.Values.Single();
        p.AddFile("/docs/existing.txt", "old"u8.ToArray());

        var newFile = Path.Combine(drive.Root, "docs", "new.txt");
        File.WriteAllText(newFile, "created on the virtual drive");
        await TestEnv.WaitUntilAsync(() => p.GetContent("/docs/new.txt") != null && env.Uploads.Get(p.AccountId, "/docs/new.txt") == null, TimeSpan.FromSeconds(30), "upload new file");
        Assert.Equal("created on the virtual drive"u8.ToArray(), p.GetContent("/docs/new.txt"));

        File.WriteAllBytes(Path.Combine(drive.Root, "docs", "existing.txt"), "replaced"u8.ToArray());
        await TestEnv.WaitUntilAsync(() => p.GetContent("/docs/existing.txt") is { } c && c.SequenceEqual("replaced"u8.ToArray()), TimeSpan.FromSeconds(30), "overwrite upload");

        File.Move(newFile, Path.Combine(drive.Root, "docs", "renamed.txt"));
        Assert.True(p.Exists("/docs/renamed.txt"));
        Assert.False(p.Exists("/docs/new.txt"));

        Directory.CreateDirectory(Path.Combine(drive.Root, "saves"));
        Assert.True(p.Exists("/saves"));

        File.Delete(Path.Combine(drive.Root, "docs", "renamed.txt"));
        Assert.False(p.Exists("/docs/renamed.txt"));
        Directory.Delete(Path.Combine(drive.Root, "saves"));
        Assert.False(p.Exists("/saves"));
        drives.Dispose();
    }

    [WinFspFact]
    public async Task UsedDriveLetter_IsRejected()
    {
        var (env, drives, drive) = await MountAsync();
        await using var envScope = env;
        Assert.True(DriveManager.IsLetterInUse(drive.Letter));
        Assert.DoesNotContain(drive.Letter, DriveManager.AvailableLetters());
        Assert.Throws<InvalidOperationException>(() => drives.Mount(new DriveMountSettings { DriveLetter = drive.Letter }));
        Assert.Throws<InvalidOperationException>(() => drives.Mount(new DriveMountSettings { DriveLetter = "C" }));
        drives.Dispose();
    }

    [WinFspFact]
    public async Task MultiAccountDrive_IsolatesAccountsInFolders()
    {
        var (env, drives, drive) = await MountAsync(allAccounts: true, setup: e =>
        {
            e.AddAccountAsync("TeraBox").GetAwaiter().GetResult();
            e.AddAccountAsync("TeraBox").GetAwaiter().GetResult();
        });
        await using var envScope = env;
        var providers = env.Accounts.Runtimes.Select(r => env.Providers[r.Id]).ToList();
        providers[0].AddFile("/same.txt", "account one"u8.ToArray());
        providers[1].AddFile("/same.txt", "account two"u8.ToArray());
        drive.Core.RebuildAccountMap();

        var folders = Directory.GetDirectories(drive.Root).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "TeraBox", "TeraBox (2)" }, folders);
        Assert.Equal("account one", File.ReadAllText(Path.Combine(drive.Root, "TeraBox", "same.txt")));
        Assert.Equal("account two", File.ReadAllText(Path.Combine(drive.Root, "TeraBox (2)", "same.txt")));
        Assert.ThrowsAny<IOException>(() => File.Move(Path.Combine(drive.Root, "TeraBox", "same.txt"), Path.Combine(drive.Root, "TeraBox (2)", "moved.txt")));
        drives.Dispose();
    }

    [WinFspFact]
    public async Task ReadOnlyMount_RefusesWrites()
    {
        var (env, drives, drive) = await MountAsync(readOnly: true);
        await using var envScope = env;
        env.Providers.Values.Single().AddFile("/a.txt", "x"u8.ToArray());
        Assert.Equal("x", File.ReadAllText(Path.Combine(drive.Root, "a.txt")));
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(drive.Root, "b.txt"), "y"));
        drives.Dispose();
    }

    [WinFspFact]
    public async Task Disconnect_CachedDataStaysReadable_AndReconnectRestoresAccess()
    {
        var (env, drives, drive) = await MountAsync();
        await using var envScope = env;
        var p = env.Providers.Values.Single();
        p.AddFile("/cached.bin", TestEnv.RandomBytes(100_000, 1));
        p.AddFile("/cold.bin", TestEnv.RandomBytes(100_000, 2));
        File.ReadAllBytes(Path.Combine(drive.Root, "cached.bin"));
        _ = new FileInfo(Path.Combine(drive.Root, "cold.bin")).Length;

        p.Offline = true;
        Assert.Equal(100_000, File.ReadAllBytes(Path.Combine(drive.Root, "cached.bin")).Length);
        Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(Path.Combine(drive.Root, "cold.bin")));

        p.Offline = false;
        Assert.Equal(100_000, File.ReadAllBytes(Path.Combine(drive.Root, "cold.bin")).Length);
        drives.Dispose();
    }

    [WinFspFact]
    public async Task Benchmark_ColdAndWarmSequentialRead()
    {
        var size = 64 * MiB;
        var data = TestEnv.RandomBytes(size, 3);
        var (env, drives, drive) = await MountAsync();
        await using var envScope = env;
        env.Providers.Values.Single().AddFile("/bench.bin", data);
        var path = Path.Combine(drive.Root, "bench.bin");

        var sw = Stopwatch.StartNew();
        var cold = File.ReadAllBytes(path);
        var coldSeconds = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var warm = File.ReadAllBytes(path);
        var warmSeconds = sw.Elapsed.TotalSeconds;
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(cold));
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(warm));

        var coldMBps = size / 1048576.0 / coldSeconds;
        var warmMBps = size / 1048576.0 / warmSeconds;
        _out.WriteLine($"cold {coldMBps:F1} MB/s, warm {warmMBps:F1} MB/s");
        Bench.Record("winfsp_seq_read_64MiB_cold_MBps", Math.Round(coldMBps, 1));
        Bench.Record("winfsp_seq_read_64MiB_warm_MBps", Math.Round(warmMBps, 1));
        Bench.Record("winfsp_cache_hit_rate_after_warm", Math.Round(env.Metrics.CacheHitRate() ?? 0, 3));

        var latencies = new List<double>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
        {
            var rnd = new Random(1);
            var buf = new byte[4096];
            for (var i = 0; i < 500; i++)
            {
                fs.Seek(rnd.NextInt64(0, size - 4096), SeekOrigin.Begin);
                var t = Stopwatch.StartNew();
                fs.ReadExactly(buf);
                latencies.Add(t.Elapsed.TotalMilliseconds);
            }
        }

        latencies.Sort();
        Bench.Record("winfsp_random_4KiB_read_warm_p50_ms", Math.Round(latencies[latencies.Count / 2], 3));
        Bench.Record("winfsp_random_4KiB_read_warm_p95_ms", Math.Round(latencies[(int)(latencies.Count * 0.95)], 3));
        drives.Dispose();
    }
}
