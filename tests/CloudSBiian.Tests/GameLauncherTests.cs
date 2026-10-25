using CloudSBiian.GameLauncher;

namespace CloudSBiian.Tests;

public class GameLauncherTests
{
    private static LibraryEntry LocalEntry(string exe, string args) => new()
    {
        Id = LibraryEntry.NewId(),
        Name = Path.GetFileNameWithoutExtension(exe),
        Executable = exe,
        Arguments = args,
    };

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Library_PersistsEntries()
    {
        await using var env = TestEnv.Create();
        var library = new LibraryStore(env.Db);
        var e = LocalEntry(Cmd, "/c exit 0");
        e.Mode = LaunchMode.FullLocal;
        e.CachePriority = 2;
        library.Save(e);
        var loaded = new LibraryStore(env.Db).Get(e.Id)!;
        Assert.Equal(e.Executable, loaded.Executable);
        Assert.Equal(LaunchMode.FullLocal, loaded.Mode);
        Assert.Equal(2, loaded.CachePriority);
        library.Remove(e.Id);
        Assert.Null(library.Get(e.Id));
    }

    [Fact]
    public async Task Launch_RecordsExitCode()
    {
        await using var env = TestEnv.Create();
        var library = new LibraryStore(env.Db);
        var launcher = new LaunchService(library, (_, _) => null, null);
        var e = LocalEntry(Cmd, "/c exit 3");
        library.Save(e);
        launcher.Launch(e);
        await TestEnv.WaitUntilAsync(() => library.Get(e.Id)!.LastExitCode == 3, TimeSpan.FromSeconds(10), "exit code recorded");
        Assert.Equal("Exited with code 3", library.Get(e.Id)!.LastStatus);
        Assert.False(launcher.IsRunning(e.Id));
    }

    [Fact]
    public async Task Stop_TerminatesTheProcessTree()
    {
        await using var env = TestEnv.Create();
        var library = new LibraryStore(env.Db);
        var launcher = new LaunchService(library, (_, _) => null, null);
        var e = LocalEntry(Cmd, "/c ping -n 60 127.0.0.1 > nul");
        library.Save(e);
        launcher.Launch(e);
        Assert.True(launcher.IsRunning(e.Id));
        Assert.Throws<InvalidOperationException>(() => launcher.Launch(e));
        await launcher.StopAsync(e.Id, TimeSpan.FromSeconds(2));
        await TestEnv.WaitUntilAsync(() => !launcher.IsRunning(e.Id), TimeSpan.FromSeconds(10), "stopped");
    }

    [Fact]
    public async Task MissingExecutable_GivesAClearError()
    {
        await using var env = TestEnv.Create();
        var library = new LibraryStore(env.Db);
        var launcher = new LaunchService(library, (_, _) => null, null);
        var e = LocalEntry(@"C:\does\not\exist\game.exe", string.Empty);
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Launch(e));
        Assert.Contains("not found", ex.Message);

        var cloud = new LibraryEntry { Id = "c1", Name = "Cloud", AccountId = "mock_000001", CloudFolder = "/Games/X", Executable = "x.exe" };
        Assert.Null(launcher.ResolveExecutable(cloud, out var error));
        Assert.Contains("Mount", error);
    }

    [Fact]
    public void Arguments_AreValidated()
    {
        Assert.Null(LaunchService.ValidateArguments("-windowed -fps 60"));
        Assert.NotNull(LaunchService.ValidateArguments("a\nb"));
    }

    [Fact]
    public async Task RestartRecovery_MarksEndedProcesses()
    {
        await using var env = TestEnv.Create();
        var library = new LibraryStore(env.Db);
        var e = LocalEntry(Cmd, "/c exit 0");
        e.LastStatus = "Running";
        e.LastPid = int.MaxValue - 7;
        library.Save(e);
        new LaunchService(library, (_, _) => null, null).RecoverAfterRestart();
        Assert.StartsWith("Ended while CloudSBiian was closed", library.Get(e.Id)!.LastStatus);
    }

    [Fact]
    public void CompatibilityScanner_FlagsAntiCheatAndLaunchers()
    {
        var results = CompatibilityScanner.Scan(new[]
        {
            ("Game.exe", false),
            ("EasyAntiCheat", true),
            ("Bin/steam_api64.dll", false),
        });
        Assert.Contains(results, r => r.Severity == CheckSeverity.Warning && r.Message.Contains("Easy Anti-Cheat"));
        Assert.Contains(results, r => r.Message.Contains("Steam"));
        Assert.Contains(CompatibilityScanner.Scan(new[] { ("x.exe", false) }), r => r.Message.Contains("does not guarantee"));
    }

    [Fact]
    public async Task Prepare_AnalyzesAndPrefetchesRecordedTrace()
    {
        await using var env = TestEnv.Create();
        var prediction = env.EnablePrediction();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/Games/G/game.exe", TestEnv.RandomBytes(10_000, 1));
        p.AddFile("/Games/G/data.pak", TestEnv.RandomBytes(3 * 1024 * 1024, 2));
        p.AddDirectory("/Games/G/EasyAntiCheat");
        var library = new LibraryStore(env.Db);
        var launcher = new LaunchService(library, (_, _) => @"X:\A\Games\G", prediction);
        var prepare = new PrepareService(env.Accounts, env.Cache, env.Uploads, env.Profiles, env.Transfers, () => env.Settings, launcher);
        var entry = new LibraryEntry { Id = "g1", Name = "G", AccountId = id, CloudFolder = "/Games/G", Executable = "game.exe", Mode = LaunchMode.HybridOptimized };
        library.Save(entry);
        env.Profiles.AddTraceChunk(entry.TraceId, id, "/Games/G/data.pak", 2, 10);
        env.Profiles.AddTraceChunk(entry.TraceId, id, "/Games/G/data.pak", 0, 20);

        var report = await prepare.AnalyzeAsync(entry, null, default);
        Assert.Contains(report.Checks, c => c.Name == "Executable" && c.Severity == CheckSeverity.Ok);
        Assert.Contains(report.Checks, c => c.Message.Contains("Easy Anti-Cheat"));
        Assert.Equal(2 * 1024 * 1024, report.BytesToPrefetch);

        var bytes = await prepare.PrefetchTraceAsync(entry, null, default);
        Assert.Equal(2 * 1024 * 1024, bytes);
        var after = await prepare.AnalyzeAsync(entry, null, default);
        Assert.Equal(0, after.BytesToPrefetch);
        Assert.True(env.Cache.IsPinned(id, "/Games/G/data.pak"));
    }

    [Fact]
    public async Task InstallLocal_CopiesAndVerifiesEveryFile()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var exe = TestEnv.RandomBytes(50_000, 1);
        var pak = TestEnv.RandomBytes(2_000_000, 2);
        p.AddFile("/G/game.exe", exe);
        p.AddFile("/G/sub/data.pak", pak);
        var library = new LibraryStore(env.Db);
        var launcher = new LaunchService(library, (_, _) => null, null);
        var prepare = new PrepareService(env.Accounts, env.Cache, env.Uploads, env.Profiles, env.Transfers, () => env.Settings, launcher);
        var entry = new LibraryEntry { Id = "g2", Name = "G", AccountId = id, CloudFolder = "/G", Executable = "game.exe" };
        var target = Path.Combine(env.Root, "installed");

        await prepare.InstallLocalAsync(entry, target, null, default);

        Assert.Equal(exe, File.ReadAllBytes(Path.Combine(target, "game.exe")));
        Assert.Equal(pak, File.ReadAllBytes(Path.Combine(target, "sub", "data.pak")));
        Assert.Equal(target, entry.LocalPath);
        entry.Mode = LaunchMode.FullLocal;
        Assert.Equal(Path.Combine(target, "game.exe"), launcher.ResolveExecutable(entry, out _));

        var calls = p.ReadCalls;
        await prepare.InstallLocalAsync(entry, target, null, default);
        Assert.Equal(calls, p.ReadCalls);
    }
}
