using System.Diagnostics;
using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Monitoring;
using CloudSBiian.PredictionEngine;

namespace CloudSBiian.Tests;

public class SchedulingTests
{
    [Fact]
    public async Task ActiveRead_PreemptsRunningPrefetch()
    {
        var limiter = new PriorityLimiter(2);
        var preempted = 0;
        limiter.Preempted += (_, _) => preempted++;
        using var p1 = await limiter.AcquireAsync(FetchPriority.P1, default);
        using var p2 = await limiter.AcquireAsync(FetchPriority.P1, default);
        var activeTask = limiter.AcquireAsync(FetchPriority.P0, default);
        Assert.Equal(1, preempted);
        Assert.True(p1.Token.IsCancellationRequested || p2.Token.IsCancellationRequested);
        p1.Dispose();
        using var active = await activeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FetchPriority.P0, active.Priority);
    }

    [Fact]
    public async Task SpeculativeWork_LeavesASlotForActiveReads()
    {
        var limiter = new PriorityLimiter(3);
        using var a = await limiter.AcquireAsync(FetchPriority.P3, default);
        using var b = await limiter.AcquireAsync(FetchPriority.P2, default);
        var third = limiter.AcquireAsync(FetchPriority.P3, default);
        await Task.Delay(100);
        Assert.False(third.IsCompleted, "The last slot is reserved for P0/P1.");
        var demand = await limiter.AcquireAsync(FetchPriority.P0, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, limiter.Running);
        demand.Dispose();
        a.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task BandwidthLimiter_CapsThroughput()
    {
        var limiter = new BandwidthLimiter(() => 1_000_000);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
        {
            await limiter.ConsumeAsync(100_000, default);
        }

        // 3 MB at 1 MB/s with a 1 s initial burst allowance: at least ~1.5 s.
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1.5), $"took {sw.Elapsed}");
    }
}

public class PredictionTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task SequentialReads_TriggerReadAhead()
    {
        await using var env = TestEnv.Create(s => s.PredictionAggressiveness = PredictionAggressiveness.Balanced);
        var prediction = env.EnablePrediction();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/movie.bin", TestEnv.RandomBytes(8 * MiB, 1));
        var e = (await p.StatAsync("/movie.bin", default))!;
        var pattern = new ReadPattern();
        var buf = new byte[256 * 1024];
        for (long offset = 0; offset < 1 * MiB; offset += buf.Length)
        {
            await env.Cache.ReadAsync(id, e, offset, buf, FetchPriority.P0, default);
            prediction.OnRead(id, e, pattern, offset, buf.Length);
        }

        var key = env.Cache.Store.GetFile(id, "/movie.bin")!.FileKey;
        await TestEnv.WaitUntilAsync(() => env.Cache.Store.Contains(id, key, 2), TimeSpan.FromSeconds(10), "read-ahead of next blocks");
        Assert.True(env.Metrics.Get(MetricNames.PrefetchIssued) >= 1);
    }

    [Fact]
    public async Task CoAccess_IsLearnedAndPrefetched()
    {
        await using var env = TestEnv.Create(s => s.PredictionAggressiveness = PredictionAggressiveness.Balanced);
        var prediction = env.EnablePrediction();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/game/launcher.exe", TestEnv.RandomBytes(100_000, 1));
        p.AddFile("/game/data.pak", TestEnv.RandomBytes(2 * MiB, 2));
        var exe = (await p.StatAsync("/game/launcher.exe", default))!;
        var pak = (await p.StatAsync("/game/data.pak", default))!;

        for (var i = 0; i < 3; i++)
        {
            prediction.OnFileOpened(id, exe);
            prediction.OnFileOpened(id, pak);
        }

        var followers = env.Profiles.Followers(id, "/game/launcher.exe", 5);
        Assert.Contains(followers, f => f.Path == "/game/data.pak" && f.Probability > 0.5);

        prediction.OnFileOpened(id, exe);
        await TestEnv.WaitUntilAsync(() => env.Cache.Store.GetFile(id, "/game/data.pak") is { } f && env.Cache.Store.Contains(id, f.FileKey, 0),
            TimeSpan.FromSeconds(10), "co-access prefetch");
    }

    [Fact]
    public async Task Disabled_DoesNotPrefetch()
    {
        await using var env = TestEnv.Create(s => s.PredictionEnabled = false);
        var prediction = env.EnablePrediction();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/a.bin", TestEnv.RandomBytes(100, 1));
        prediction.Enqueue(new PrefetchRequest(id, "/a.bin", null, FetchPriority.P1, "test"));
        await Task.Delay(500);
        Assert.Equal(0, env.Cache.Store.TotalPhysicalBytes);
    }

    [Fact]
    public async Task LaunchTrace_RecordsActiveReadsInOrder()
    {
        await using var env = TestEnv.Create();
        var prediction = env.EnablePrediction();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/g/a.bin", TestEnv.RandomBytes(3 * MiB, 1));
        var e = (await p.StatAsync("/g/a.bin", default))!;
        prediction.StartTrace("lib:test");
        await env.Cache.ReadAsync(id, e, 2 * MiB, new byte[100], FetchPriority.P0, default);
        await Task.Delay(20);
        await env.Cache.ReadAsync(id, e, 0, new byte[100], FetchPriority.P0, default);
        prediction.StopTrace("lib:test");
        await env.Cache.ReadAsync(id, e, MiB, new byte[100], FetchPriority.P0, default);

        var trace = env.Profiles.GetTrace("lib:test");
        Assert.Equal(new[] { 2, 0 }, trace.Select(t => t.ChunkIndex).ToArray());
    }
}
