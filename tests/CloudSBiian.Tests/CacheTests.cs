using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Tests;

public class CacheTests
{
    private const int MiB = 1024 * 1024;

    private static async Task<byte[]> ReadAllAsync(TestEnv env, string account, CloudEntry e)
    {
        var buf = new byte[e.Size];
        var n = await env.Cache.ReadAsync(account, e, 0, buf, FetchPriority.P0, default);
        Assert.Equal(e.Size, n);
        return buf;
    }

    [Fact]
    public async Task ReadThrough_ReturnsExactBytes_SecondReadIsAHit()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var data = TestEnv.RandomBytes(3 * MiB + 123, 1);
        p.AddFile("/big.bin", data);
        var e = (await p.StatAsync("/big.bin", default))!;

        Assert.Equal(data, await ReadAllAsync(env, id, e));
        var callsAfterFirst = p.ReadCalls;
        Assert.Equal(data, await ReadAllAsync(env, id, e));
        Assert.Equal(callsAfterFirst, p.ReadCalls);
        Assert.True(env.Metrics.Get(MetricNames.CacheHits) >= 4);
    }

    [Fact]
    public async Task RandomReads_MatchSource()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var data = TestEnv.RandomBytes(5 * MiB + 77, 2);
        p.AddFile("/r.bin", data);
        var e = (await p.StatAsync("/r.bin", default))!;
        var rnd = new Random(3);
        for (var i = 0; i < 300; i++)
        {
            var offset = rnd.NextInt64(0, data.Length);
            var len = rnd.Next(1, 3 * MiB);
            var buf = new byte[len];
            var n = await env.Cache.ReadAsync(id, e, offset, buf, FetchPriority.P0, default);
            Assert.Equal((int)Math.Min(len, data.Length - offset), n);
            Assert.True(buf.AsSpan(0, n).SequenceEqual(data.AsSpan((int)offset, n)), $"Mismatch at offset {offset} len {len}");
        }

        Assert.Equal(0, await env.Cache.ReadAsync(id, e, data.Length, new byte[10], FetchPriority.P0, default));
    }

    [Fact]
    public async Task Budget_IsEnforcedWithLruEviction()
    {
        await using var env = TestEnv.Create(s => s.CacheBudgetBytes = 3 * MiB);
        var (id, p) = await env.AddAccountAsync("A");
        for (var i = 0; i < 6; i++)
        {
            p.AddFile($"/f{i}.bin", TestEnv.RandomBytes(MiB, 100 + i));
        }

        for (var i = 0; i < 6; i++)
        {
            var e = (await p.StatAsync($"/f{i}.bin", default))!;
            await ReadAllAsync(env, id, e);
            await Task.Delay(5);
            env.Cache.Store.FlushAccessTimes();
        }

        Assert.True(env.Cache.Store.TotalPhysicalBytes <= 3 * MiB, $"physical {env.Cache.Store.TotalPhysicalBytes}");
        Assert.True(env.Metrics.Get(MetricNames.CacheEvictions) >= 3);
        var newest = env.Cache.Store.GetFile(id, "/f5.bin")!;
        Assert.True(env.Cache.Store.Contains(id, newest.FileKey, 0), "Most recently used block must survive eviction.");
        var oldest = env.Cache.Store.GetFile(id, "/f0.bin")!;
        Assert.False(env.Cache.Store.Contains(id, oldest.FileKey, 0), "Least recently used block must be evicted first.");
    }

    [Fact]
    public async Task PinnedData_IsNeverEvicted()
    {
        await using var env = TestEnv.Create(s => s.CacheBudgetBytes = 2 * MiB);
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/keep/game.pak", TestEnv.RandomBytes(MiB, 1));
        for (var i = 0; i < 4; i++)
        {
            p.AddFile($"/tmp{i}.bin", TestEnv.RandomBytes(MiB, 10 + i));
        }

        env.Cache.Pin(id, "/keep");
        await ReadAllAsync(env, id, (await p.StatAsync("/keep/game.pak", default))!);
        for (var i = 0; i < 4; i++)
        {
            await ReadAllAsync(env, id, (await p.StatAsync($"/tmp{i}.bin", default))!);
        }

        var pinned = env.Cache.Store.GetFile(id, "/keep/game.pak")!;
        Assert.True(env.Cache.Store.Contains(id, pinned.FileKey, 0));
    }

    [Fact]
    public async Task CorruptBlock_IsDetectedDiscardedAndRefetched()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var data = TestEnv.RandomBytes(MiB, 5);
        p.AddFile("/c.bin", data);
        var e = (await p.StatAsync("/c.bin", default))!;
        await ReadAllAsync(env, id, e);

        var block = Directory.GetFiles(env.Cache.Store.AccountDirectory(id), "*.bin", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(block);
        bytes[1000] ^= 0xFF;
        File.WriteAllBytes(block, bytes);
        env.Cache.Store.DropHotCache();

        Assert.Equal(data, await ReadAllAsync(env, id, e));
        Assert.Equal(1, env.Metrics.Get(MetricNames.CacheCorruptChunks));
    }

    [Fact]
    public async Task ChangedSourceFile_InvalidatesCachedBlocks()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/doc.txt", "version-1"u8.ToArray(), DateTimeOffset.UtcNow.AddMinutes(-5));
        var v1 = (await p.StatAsync("/doc.txt", default))!;
        Assert.Equal("version-1"u8.ToArray(), await ReadAllAsync(env, id, v1));

        p.AddFile("/doc.txt", "version-two"u8.ToArray());
        var v2 = (await p.StatAsync("/doc.txt", default))!;
        Assert.NotEqual(v1.Identity, v2.Identity);
        Assert.Equal("version-two"u8.ToArray(), await ReadAllAsync(env, id, v2));
    }

    [Fact]
    public async Task SamePathInTwoAccounts_IsCachedSeparately()
    {
        await using var env = TestEnv.Create();
        var (a, pa) = await env.AddAccountAsync("A");
        var (b, pb) = await env.AddAccountAsync("B");
        pa.AddFile("/x.bin", TestEnv.RandomBytes(4096, 1));
        pb.AddFile("/x.bin", TestEnv.RandomBytes(4096, 2));
        var ra = await ReadAllAsync(env, a, (await pa.StatAsync("/x.bin", default))!);
        var rb = await ReadAllAsync(env, b, (await pb.StatAsync("/x.bin", default))!);
        Assert.NotEqual(ra, rb);
        Assert.StartsWith(env.Cache.Store.AccountDirectory(a), Directory.GetFiles(env.Cache.Store.AccountDirectory(a), "*.bin", SearchOption.AllDirectories).Single());
        env.Cache.Store.ClearAccount(a);
        Assert.Equal(rb, await ReadAllAsync(env, b, (await pb.StatAsync("/x.bin", default))!));
        Assert.True(env.Cache.Store.PhysicalBytes(b) > 0);
    }

    [Fact]
    public async Task BackendWithoutRanges_IsDownloadedSequentiallyOnce()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("NoRange", supportsRange: false);
        var data = TestEnv.RandomBytes(3 * MiB + 5, 8);
        p.AddFile("/v.bin", data);
        var e = (await p.StatAsync("/v.bin", default))!;
        var buf = new byte[1000];
        await env.Cache.ReadAsync(id, e, 2 * MiB + 10, buf, FetchPriority.P0, default);
        Assert.True(buf.AsSpan().SequenceEqual(data.AsSpan(2 * MiB + 10, 1000)));
        Assert.Equal(data, await ReadAllAsync(env, id, e));
        Assert.Equal(1, p.ReadCalls);
    }

    [Fact]
    public async Task FreeSpaceFloor_PreventsCachingButReadsStillWork()
    {
        await using var env = TestEnv.Create(s => s.MinFreeDiskBytes = long.MaxValue / 4);
        var (id, p) = await env.AddAccountAsync("A");
        var data = TestEnv.RandomBytes(2 * MiB, 4);
        p.AddFile("/f.bin", data);
        var e = (await p.StatAsync("/f.bin", default))!;
        Assert.Equal(data, await ReadAllAsync(env, id, e));
        Assert.Equal(0, env.Cache.Store.TotalPhysicalBytes);
    }

    [Fact]
    public async Task ExcludedFiles_AreNeverCached()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        env.Cache.AddExclusion(null, "*.iso");
        env.Cache.AddExclusion(id, "/Videos/**");
        p.AddFile("/disk.iso", TestEnv.RandomBytes(MiB, 1));
        p.AddFile("/Videos/a/b.mp4", TestEnv.RandomBytes(MiB, 2));
        p.AddFile("/keep.txt", TestEnv.RandomBytes(100, 3));
        await ReadAllAsync(env, id, (await p.StatAsync("/disk.iso", default))!);
        await ReadAllAsync(env, id, (await p.StatAsync("/Videos/a/b.mp4", default))!);
        Assert.Equal(0, env.Cache.Store.TotalPhysicalBytes);
        await ReadAllAsync(env, id, (await p.StatAsync("/keep.txt", default))!);
        Assert.True(env.Cache.Store.TotalPhysicalBytes > 0);
    }

    [Fact]
    public async Task ClearingTheCache_KeepsCloudDataAndPendingUploads()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/a.bin", TestEnv.RandomBytes(MiB, 1));
        await ReadAllAsync(env, id, (await p.StatAsync("/a.bin", default))!);
        var staging = env.Staging.LocalPathFor(id, "/new.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        File.WriteAllText(staging, "local change");
        env.Accounts.Get(id)!.GetType();
        await env.Accounts.DisconnectAsync(id);
        env.Uploads.Enqueue(id, "/new.txt", staging, 12, null);

        env.Cache.ClearAllBlocks();

        Assert.Equal(0, env.Cache.Store.TotalPhysicalBytes);
        Assert.True(p.Exists("/a.bin"));
        Assert.True(File.Exists(staging));
        Assert.NotNull(env.Uploads.Get(id, "/new.txt"));
    }

    [Fact]
    public async Task VerifyAll_RemovesOrphansAndMissingBlocks()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/a.bin", TestEnv.RandomBytes(2 * MiB, 1));
        await ReadAllAsync(env, id, (await p.StatAsync("/a.bin", default))!);
        var blocks = Directory.GetFiles(env.Cache.Store.AccountDirectory(id), "*.bin", SearchOption.AllDirectories);
        File.Delete(blocks[0]);
        var orphan = Path.Combine(Path.GetDirectoryName(blocks[1])!, "deadbeef_0.bin");
        File.WriteAllText(orphan, "x");
        var (checkedCount, removed) = env.Cache.Store.VerifyAll(default);
        Assert.Equal(2, checkedCount);
        Assert.Equal(2, removed);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task CacheSurvivesRestart_AndTempFilesAreCleaned()
    {
        var root = Path.Combine(Path.GetTempPath(), "csb-tests", Guid.NewGuid().ToString("N"));
        await using (var env = TestEnv.Create())
        {
            var (id, p) = await env.AddAccountAsync("A");
            p.AddFile("/a.bin", TestEnv.RandomBytes(MiB, 9));
            var e = (await p.StatAsync("/a.bin", default))!;
            await ReadAllAsync(env, id, e);
            var tmp = Path.Combine(env.Cache.Store.Root, id, "chunks", "zz", "x.bin.tmp-1-abc");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            File.WriteAllText(tmp, "partial");
            env.Cache.Dispose();

            var restarted = new CacheService(env.Cache.CacheDirectory, env.Db, env.Accounts, env.Metrics, env.Transfers, () => env.Settings);
            Assert.False(File.Exists(tmp));
            var before = p.ReadCalls;
            var buf = new byte[e.Size];
            await restarted.ReadAsync(id, e, 0, buf, FetchPriority.P0, default);
            Assert.Equal(before, p.ReadCalls);
            restarted.Dispose();
        }
    }

    [Fact]
    public void GlobPatterns_MatchAsDocumented()
    {
        Assert.Matches(CacheService.GlobToRegex("*.tmp"), "a.tmp");
        Assert.DoesNotMatch(CacheService.GlobToRegex("*.tmp"), "a.tmp.bak");
        Assert.Matches(CacheService.GlobToRegex("/Videos/**"), "/Videos/x/y.mp4");
        Assert.DoesNotMatch(CacheService.GlobToRegex("/Videos/**"), "/Music/y.mp3");
    }
}
