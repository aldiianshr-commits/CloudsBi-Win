using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.Tests;

/// <summary>File system semantics of the drive, exercised without the WinFsp kernel driver.</summary>
public class DriveCoreTests
{
    private const int Ok = 0;

    private static DriveCore Core(TestEnv env, params string[] accountIds) =>
        new(env.DriveServices(), new DriveMountSettings { DriveLetter = "X", AccountIds = accountIds.ToList() });

    [Fact]
    public async Task MultiAccountDrive_ShowsOneFolderPerAccount()
    {
        await using var env = TestEnv.Create();
        await env.AddAccountAsync("TeraBox");
        await env.AddAccountAsync("TeraBox");
        await env.AddAccountAsync("Work: Drive");
        var core = Core(env);
        Assert.Equal(NtStatusSuccess(), core.Open("\\", out var root));
        var names = core.ListDirectory(root!).Select(e => e.Name).ToList();
        Assert.Equal(new[] { "TeraBox", "TeraBox (2)", "Work_ Drive" }, names);
        core.Close(root!);
    }

    [Fact]
    public async Task ReadFile_ThroughDrive_MatchesSource()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var data = TestEnv.RandomBytes(2_500_000, 1);
        p.AddFile("/Games/Game/data.pak", data);
        var core = Core(env, id);
        Assert.True(core.GetInfo("\\Games\\Game")!.Value.IsDirectory);
        Assert.Equal(Ok, core.Open("\\Games\\Game\\data.pak", out var node));
        var buffer = new byte[1_000_000];
        Assert.Equal(Ok, core.Read(node!, 1_200_000, buffer, out var read));
        Assert.Equal(1_000_000, read);
        Assert.True(buffer.AsSpan().SequenceEqual(data.AsSpan(1_200_000, 1_000_000)));
        Assert.NotEqual(Ok, core.Read(node!, data.Length, buffer, out _));
        core.Close(node!);
        Assert.Equal(0, core.OpenHandles);
    }

    [Fact]
    public async Task MissingFiles_ReportNameOrPathNotFound()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddDirectory("/dir");
        var core = Core(env, id);
        Assert.NotEqual(Ok, core.Open("\\dir\\nope.txt", out _));
        Assert.NotEqual(Ok, core.Open("\\nodir\\nope.txt", out _));
        Assert.Null(core.GetInfo("\\dir\\nope.txt"));
    }

    [Fact]
    public async Task CreateWriteClose_QueuesUpload_AndFileAppearsInCloud()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddDirectory("/saves");
        env.Uploads.Start();
        var core = Core(env, id);
        Assert.Equal(Ok, core.Create("\\saves\\slot1.sav", false, out var node));
        Assert.Equal(Ok, core.Write(node!, 0, "hello "u8, false, false, out _));
        Assert.Equal(Ok, core.Write(node!, 0, "world"u8, true, false, out _));
        Assert.Contains(core.ListDirectory(OpenDir(core, "\\saves")), e => e.Name == "slot1.sav" && e.Info.Pending);
        core.Cleanup(node!, delete: false);
        core.Close(node!);

        await TestEnv.WaitUntilAsync(() => p.GetContent("/saves/slot1.sav") != null && env.Uploads.Get(id, "/saves/slot1.sav") == null,
            TimeSpan.FromSeconds(20), "upload of new file");
        Assert.Equal("hello world"u8.ToArray(), p.GetContent("/saves/slot1.sav"));
    }

    [Fact]
    public async Task PartialWrite_ToExistingFile_KeepsTheRestOfTheContent()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var original = TestEnv.RandomBytes(3_000_000, 4);
        p.AddFile("/config.bin", original);
        env.Uploads.Start();
        var core = Core(env, id);
        Assert.Equal(Ok, core.Open("\\config.bin", out var node));
        Assert.Equal(Ok, core.Write(node!, 1_500_000, new byte[] { 1, 2, 3 }, false, false, out _));
        core.Cleanup(node!, false);
        core.Close(node!);
        await TestEnv.WaitUntilAsync(() => env.Uploads.GetAll().Count == 0, TimeSpan.FromSeconds(20), "upload");

        var expected = (byte[])original.Clone();
        expected[1_500_000] = 1;
        expected[1_500_001] = 2;
        expected[1_500_002] = 3;
        Assert.Equal(expected, p.GetContent("/config.bin"));
    }

    [Fact]
    public async Task RenameAndDelete_ArePropagatedToTheCloud()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/a.txt", "x"u8.ToArray());
        var core = Core(env, id);
        Assert.Equal(Ok, core.Open("\\a.txt", out var node));
        Assert.Equal(Ok, core.Rename(node!, "\\b.txt", false));
        core.Close(node!);
        Assert.False(p.Exists("/a.txt"));
        Assert.True(p.Exists("/b.txt"));

        Assert.Equal(Ok, core.Open("\\b.txt", out var del));
        Assert.Equal(Ok, core.CanDelete(del!));
        core.Cleanup(del!, delete: true);
        core.Close(del!);
        Assert.False(p.Exists("/b.txt"));
    }

    [Fact]
    public async Task CaseInsensitivePaths_MapToTheRealCloudSpelling()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/Docs/Report.TXT", "content"u8.ToArray());
        var core = Core(env, id);
        Assert.Equal(Ok, core.Open("\\DOCS\\report.txt", out var node));
        Assert.Equal("/Docs/Report.TXT", node!.CloudPath);
        Assert.Equal(Ok, core.Rename(node, "\\DOCS\\Final.txt", false));
        Assert.True(p.Exists("/Docs/Final.txt"));
        Assert.False(p.Exists("/DOCS/Final.txt"));
        Assert.Equal(Ok, core.Open("\\docs\\final.txt", out var n2));
        Assert.Equal(Ok, core.Rename(n2!, "\\docs\\FINAL.txt", false));
        Assert.True(p.Exists("/Docs/FINAL.txt"), "case-only rename must be applied");
    }

    [Fact]
    public async Task RenamingAFileThatWasNeverUploaded_StaysLocal()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        var core = Core(env, id);
        Assert.Equal(Ok, core.Create("\\draft.txt", false, out var node));
        core.Write(node!, 0, "draft"u8, false, false, out _);
        Assert.Equal(Ok, core.Rename(node!, "\\final.txt", false));
        core.Cleanup(node!, false);
        core.Close(node!);
        Assert.NotNull(env.Uploads.Get(id, "/final.txt"));
        Assert.Null(env.Uploads.Get(id, "/draft.txt"));
        Assert.Equal(0, p.UploadCalls);
    }

    [Fact]
    public async Task NonEmptyFolder_CannotBeDeleted_AndRootIsProtected()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/dir/file.txt", "x"u8.ToArray());
        var core = Core(env, id);
        Assert.Equal(Ok, core.Open("\\dir", out var dir));
        Assert.NotEqual(Ok, core.CanDelete(dir!));
        Assert.Equal(Ok, core.Open("\\", out var root));
        Assert.NotEqual(Ok, core.CanDelete(root!));
    }

    [Fact]
    public async Task ReadOnlyMount_RejectsWrites()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/a.txt", "x"u8.ToArray());
        var core = new DriveCore(env.DriveServices(), new DriveMountSettings { AccountIds = { id }, ReadOnly = true });
        Assert.NotEqual(Ok, core.Create("\\new.txt", false, out _));
        Assert.Equal(Ok, core.Open("\\a.txt", out var node));
        Assert.NotEqual(Ok, core.Write(node!, 0, "y"u8, false, false, out _));
        Assert.True(core.GetInfo("\\a.txt")!.Value.ReadOnly);
    }

    [Fact]
    public async Task NamesWindowsCannotShow_AreHidden_AndTraversalIsRejected()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/ok.txt", "1"u8.ToArray());
        p.AddFile("/bad:name.txt", "2"u8.ToArray());
        var core = Core(env, id);
        var names = core.ListDirectory(OpenDir(core, "\\")).Select(e => e.Name).ToList();
        Assert.Contains("ok.txt", names);
        Assert.DoesNotContain("bad:name.txt", names);
        Assert.ThrowsAny<ArgumentException>(() => core.Open("\\..\\..\\Windows\\win.ini", out _));
    }

    [Fact]
    public async Task OfflineAccount_ServesCachedData_AndFailsCleanlyOtherwise()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.AddFile("/cached.bin", TestEnv.RandomBytes(10_000, 1));
        p.AddFile("/uncached.bin", TestEnv.RandomBytes(10_000, 2));
        var core = Core(env, id);
        Assert.Equal(Ok, core.Open("\\cached.bin", out var n1));
        Assert.Equal(Ok, core.Open("\\uncached.bin", out var n2));
        core.Read(n1!, 0, new byte[10_000], out _);

        p.Offline = true;
        Assert.Equal(Ok, core.Read(n1!, 0, new byte[10_000], out var read));
        Assert.Equal(10_000, read);
        Assert.ThrowsAny<Exception>(() => core.Read(n2!, 0, new byte[100], out _));

        p.Offline = false;
        Assert.Equal(Ok, core.Read(n2!, 0, new byte[100], out _));
    }

    [Fact]
    public async Task VolumeSize_UsesReportedQuota()
    {
        await using var env = TestEnv.Create();
        var (id, p) = await env.AddAccountAsync("A");
        p.Quota = new QuotaInfo(1000, 400, 600);
        await env.Accounts.RefreshAsync(id, default);
        var core = Core(env, id);
        Assert.Equal((1000UL, 600UL), core.VolumeSize());
    }

    private static DriveNode OpenDir(DriveCore core, string path)
    {
        Assert.Equal(Ok, core.Open(path, out var node));
        return node!;
    }

    private static int NtStatusSuccess() => Ok;
}
