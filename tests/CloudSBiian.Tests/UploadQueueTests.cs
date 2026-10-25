using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.Tests;

public class UploadQueueTests
{
    private static string Stage(TestEnv env, string account, string path, byte[] content)
    {
        var file = env.Staging.LocalPathFor(account, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, content);
        return file;
    }

    [Fact]
    public async Task QueuedFile_IsUploadedAndStagingRemoved()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var content = TestEnv.RandomBytes(200_000, 1);
        var staging = Stage(env, id, "/docs/new.bin", content);
        p.AddDirectory("/docs");
        env.Uploads.Start();
        var item = env.Uploads.Enqueue(id, "/docs/new.bin", staging, content.Length, null);
        Assert.Equal(UploadState.PendingUpload, item.State);

        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/docs/new.bin") == null, TimeSpan.FromSeconds(20), "upload to finish");
        Assert.Equal(content, p.GetContent("/docs/new.bin"));
        Assert.False(File.Exists(staging));
        Assert.Contains(env.Uploads.RecentlySynced, s => s.Path == "/docs/new.bin" && s.State == UploadState.Synced);
    }

    [Fact]
    public async Task TransientFailures_AreRetriedAutomatically()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var staging = Stage(env, id, "/r.bin", new byte[] { 1, 2, 3 });
        p.FailNext(1, CloudErrorKind.RateLimited);
        env.Uploads.Start();
        env.Uploads.Enqueue(id, "/r.bin", staging, 3, null);
        await TestEnv.WaitUntilAsync(() => p.GetContent("/r.bin") != null, TimeSpan.FromSeconds(30), "retry after rate limit");
        Assert.Equal(new byte[] { 1, 2, 3 }, p.GetContent("/r.bin"));
    }

    [Fact]
    public async Task NetworkFailure_MovesToOffline_AndKeepsData()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var staging = Stage(env, id, "/o.bin", new byte[] { 9 });
        p.Offline = true;
        env.Uploads.Start();
        env.Uploads.Enqueue(id, "/o.bin", staging, 1, null);
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/o.bin")?.State == UploadState.Offline, TimeSpan.FromSeconds(20), "offline state");
        Assert.True(File.Exists(staging));
        Assert.True(env.Uploads.Get(id, "/o.bin")!.Attempts >= 1);

        p.Offline = false;
        env.Uploads.RetryAll();
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/o.bin") == null, TimeSpan.FromSeconds(20), "upload after reconnect");
        Assert.Equal(new byte[] { 9 }, p.GetContent("/o.bin"));
    }

    [Fact]
    public async Task ExpiredSession_RequiresManualRetry()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var staging = Stage(env, id, "/x.bin", new byte[] { 1 });
        p.SessionExpired = true;
        env.Uploads.Start();
        var item = env.Uploads.Enqueue(id, "/x.bin", staging, 1, null);
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/x.bin")?.State == UploadState.RetryRequired, TimeSpan.FromSeconds(20), "retry required");
        Assert.True(File.Exists(staging));

        p.SessionExpired = false;
        await env.Accounts.ReconnectAsync(id, false, default);
        env.Uploads.Retry(item.Id);
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/x.bin") == null, TimeSpan.FromSeconds(20), "upload after retry");
    }

    [Fact]
    public async Task RemoteChangeSinceOpen_IsAConflict_NotAnOverwrite()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/shared.txt", "original"u8.ToArray(), DateTimeOffset.UtcNow.AddMinutes(-1));
        var baseIdentity = (await p.StatAsync("/shared.txt", default))!.Identity;
        p.AddFile("/shared.txt", "changed on another PC"u8.ToArray());
        var staging = Stage(env, id, "/shared.txt", "my local edit"u8.ToArray());
        env.Uploads.Start();
        var item = env.Uploads.Enqueue(id, "/shared.txt", staging, 13, baseIdentity);

        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/shared.txt")?.State == UploadState.Conflict, TimeSpan.FromSeconds(20), "conflict");
        Assert.Equal("changed on another PC"u8.ToArray(), p.GetContent("/shared.txt"));

        await env.Uploads.ResolveConflictAsync(item.Id, ConflictResolution.KeepBoth, default);
        await TestEnv.WaitUntilAsync(() => env.Uploads.GetAll(id).Count == 0, TimeSpan.FromSeconds(20), "keep-both upload");
        Assert.Equal("changed on another PC"u8.ToArray(), p.GetContent("/shared.txt"));
        var conflictCopy = (await p.ListAsync("/", default)).Single(e => e.Name.StartsWith("shared (conflict", StringComparison.Ordinal));
        Assert.Equal("my local edit"u8.ToArray(), p.GetContent(conflictCopy.Path));
    }

    [Fact]
    public async Task KeepLocal_OverwritesAfterExplicitDecision()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/s.txt", "theirs"u8.ToArray());
        var staging = Stage(env, id, "/s.txt", "mine"u8.ToArray());
        env.Uploads.Start();
        var item = env.Uploads.Enqueue(id, "/s.txt", staging, 4, "stale-identity");
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/s.txt")?.State == UploadState.Conflict, TimeSpan.FromSeconds(20), "conflict");
        await env.Uploads.ResolveConflictAsync(item.Id, ConflictResolution.KeepLocal, default);
        await TestEnv.WaitUntilAsync(() => env.Uploads.Get(id, "/s.txt") == null, TimeSpan.FromSeconds(20), "upload");
        Assert.Equal("mine"u8.ToArray(), p.GetContent("/s.txt"));
    }

    [Fact]
    public async Task InterruptedUploads_AreResumedAfterRestart()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var staging = Stage(env, id, "/crash.bin", new byte[] { 5, 5 });
        var item = env.Uploads.Enqueue(id, "/crash.bin", staging, 2, null);
        env.Db.Execute("UPDATE upload_queue SET state='Uploading' WHERE id=@id", ("@id", item.Id));

        using var restarted = new UploadQueue(env.Db, env.Staging, env.Accounts, env.Transfers, env.Cache.Metadata, env.Cache.Store, () => 8);
        Assert.Equal(UploadState.PendingUpload, restarted.Get(item.Id)!.State);
        restarted.Start();
        await TestEnv.WaitUntilAsync(() => p.GetContent("/crash.bin") != null, TimeSpan.FromSeconds(20), "resume");
    }

    [Fact]
    public async Task UnqueuedStagingFiles_CanBeFoundForRecovery()
    {
        await using var env = TestEnv.Create();
        var (id, _) = await env.AddAccountAsync("A");
        using (var f = env.Staging.Acquire(id, "/unsaved.doc"))
        {
            f.Write(0, new byte[] { 1, 2, 3 });
            f.Hydrated = true;
            env.Staging.WriteSidecar(f);
        }

        var found = env.Staging.FindUnqueued((a, p) => env.Uploads.Get(a, p) != null);
        Assert.Contains(found, x => x.Meta.Path == "/unsaved.doc" && x.Meta.AccountId == id);
    }
}
