using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CloudSBiian.CloudProviders;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Security;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.Tests;

/// <summary>
/// rclone dependency management without network or a real rclone: locating, version parsing,
/// checksum verification and the honest provider status derived from the check.
/// </summary>
[Collection("rclone-locator")]
public sealed class RcloneDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csb-rclone-dep-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousManaged = RcloneLocator.ManagedDirectory;

    public RcloneDependencyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        RcloneLocator.ManagedDirectory = _previousManaged;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string FakeExe(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rclone.exe");
        File.WriteAllText(path, "not a real program");
        return path;
    }

    [Theory]
    [InlineData("rclone v1.71.2", 1, 71, 2)]
    [InlineData("rclone v1.60.0-DEV", 1, 60, 0)]
    [InlineData("rclone 1.65", 1, 65, 0)]
    [InlineData("rclone v1.68.1-terabox-fork", 1, 68, 1)]
    public void ParseVersion_ReadsRcloneVersionLine(string line, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), RcloneLocator.ParseVersion(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INFO: Could not find files for the given pattern(s).")]
    public void ParseVersion_RejectsNonRcloneOutput(string? line)
    {
        Assert.Null(RcloneLocator.ParseVersion(line));
    }

    [Fact]
    public void Locate_UsesSettingsPath_FileOrFolder()
    {
        var exe = FakeExe(Path.Combine(_root, "custom"));
        var byFile = RcloneLocator.Locate(exe);
        Assert.Equal(exe, byFile.Path);
        Assert.Equal(RcloneLocator.SettingsSource, byFile.Source);
        Assert.Null(byFile.ConfiguredPathProblem);

        var byFolder = RcloneLocator.Locate(Path.Combine(_root, "custom"));
        Assert.Equal(exe, byFolder.Path);
    }

    [Fact]
    public void Locate_FindsCloudSBiianDownload_WhenNothingIsBundled()
    {
        var managed = Path.Combine(_root, "tools", "rclone");
        RcloneLocator.ManagedDirectory = managed;
        var exe = FakeExe(managed);

        var location = RcloneLocator.Locate(null);
        // Nothing is bundled next to the test binaries, so the managed copy is the first hit.
        Assert.Equal(exe, location.Path);
        Assert.Equal("downloaded by CloudSBiian", location.Source);
        Assert.Contains(location.Searched, c => c.Source == "bundled with CloudSBiian" && !c.Exists);
        Assert.Contains(location.Searched, c => c.Source.StartsWith("PATH", StringComparison.Ordinal) || c.Source == "Chocolatey" || c.Source == "winget");
    }

    [Fact]
    public void Locate_ReportsMissingSettingsPath_AndKeepsSearching()
    {
        var managed = Path.Combine(_root, "tools", "rclone");
        RcloneLocator.ManagedDirectory = managed;
        var exe = FakeExe(managed);
        var missing = Path.Combine(_root, "nope", "rclone.exe");

        var location = RcloneLocator.Locate(missing);
        Assert.NotNull(location.ConfiguredPathProblem);
        Assert.Contains(location.Searched, c => c.Source == RcloneLocator.SettingsSource && !c.Exists && c.Path == missing);
        Assert.Equal(exe, location.Path);
    }

    [Fact]
    public async Task Validate_RejectsProgramsThatAreNotRclone()
    {
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        var (version, _, error) = await RcloneLocator.ValidateAsync(where, default);
        Assert.Null(version);
        Assert.NotNull(error);

        var (_, _, missingError) = await RcloneLocator.ValidateAsync(Path.Combine(_root, "missing.exe"), default);
        Assert.Contains("not found", missingError);
    }

    private AccountManager NewManager(Func<string?, RcloneLocation> locate, string? settingsPath = null)
    {
        var db = new AppDatabase(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
        return new AccountManager(new AccountStore(db), new InMemorySecretStore(), () => settingsPath) { RcloneLocate = locate };
    }

    [Fact]
    public async Task Check_WhenRcloneIsMissing_BlocksOnlyRcloneProviders_WithActionableReason()
    {
        var searched = new[] { new RcloneCandidate(@"C:\x\rclone\rclone.exe", "bundled with CloudSBiian", false), new RcloneCandidate(@"C:\y\rclone.exe", "PATH", false) };
        await using var am = NewManager(_ => new RcloneLocation(null, null, searched, null));
        var events = new List<RcloneState>();
        am.RcloneStatusChanged += (_, s) => events.Add(s.State);

        var status = await am.CheckRcloneAsync(default);

        Assert.Equal(RcloneState.Missing, status.State);
        Assert.True(status.NeedsAction);
        Assert.Equal(2, status.Searched.Count);
        Assert.Equal(new[] { RcloneState.Checking, RcloneState.Missing }, events);
        Assert.Null(await am.GetCatalogAsync(default));
        foreach (var kind in new[] { ProviderKind.GoogleDrive, ProviderKind.OneDrive, ProviderKind.Dropbox, ProviderKind.Sftp, ProviderKind.S3, ProviderKind.Smb, ProviderKind.TeraBox, ProviderKind.GenericRclone })
        {
            var (s, note) = am.EffectiveStatus(kind);
            Assert.Equal(ImplementationStatus.Blocked, s);
            Assert.Contains("Download rclone", note);
        }

        Assert.Equal(ImplementationStatus.Supported, am.EffectiveStatus(ProviderKind.WebDav).Status);
        Assert.Equal(ImplementationStatus.Supported, am.EffectiveStatus(ProviderKind.LocalFolder).Status);

        var ex = await Assert.ThrowsAsync<CloudProviderException>(() => am.AuthorizeAsync("drive", null, default));
        Assert.Equal(CloudErrorKind.DependencyMissing, ex.Kind);
    }

    [Fact]
    public async Task Check_WhenSettingsPointsToSomethingElse_IsUnusable_AndDoesNotFallBack()
    {
        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        var managed = Path.Combine(_root, "tools", "rclone");
        var other = FakeExe(managed);
        await using var am = NewManager(_ => new RcloneLocation(where, RcloneLocator.SettingsSource,
            [new RcloneCandidate(where, RcloneLocator.SettingsSource, true), new RcloneCandidate(other, "downloaded by CloudSBiian", true)], null), where);

        var status = await am.CheckRcloneAsync(default);

        Assert.Equal(RcloneState.Unusable, status.State);
        Assert.Equal(where, status.ExecutablePath);
        Assert.Equal(RcloneLocator.SettingsSource, status.Source);
        var (s, note) = am.EffectiveStatus(ProviderKind.Dropbox);
        Assert.Equal(ImplementationStatus.Blocked, s);
        Assert.Contains("rclone version", note);
    }

    [Fact]
    public async Task Check_SkipsBrokenCopies_AndReportsWhenNoneWork()
    {
        var a = FakeExe(Path.Combine(_root, "a"));
        var b = FakeExe(Path.Combine(_root, "b"));
        await using var am = NewManager(_ => new RcloneLocation(a, "bundled with CloudSBiian",
            [new RcloneCandidate(a, "bundled with CloudSBiian", true), new RcloneCandidate(b, "PATH", true)], null));

        var status = await am.CheckRcloneAsync(default);

        Assert.Equal(RcloneState.Unusable, status.State);
        Assert.Contains("2 copies found, none usable", status.Message);
    }

    private static RcloneBackendCatalog Catalog(params string[] backends) =>
        new("v1.71.2", backends.Select(b => new RcloneBackendInfo(b, b, [])));

    [Fact]
    public void Effective_UsesTheRealBackendList()
    {
        var official = Catalog("drive", "onedrive", "dropbox", "sftp", "s3", "smb", "webdav", "local");
        var ready = new RcloneStatus(RcloneState.Ready, "rclone.exe", "PATH", "v1.71.2", 8, [], "ok", DateTimeOffset.Now);

        Assert.Equal(ImplementationStatus.Experimental, ProviderRegistry.Effective(ProviderRegistry.Get(ProviderKind.GoogleDrive), official, ready).Status);
        var (tera, teraNote) = ProviderRegistry.Effective(ProviderRegistry.Get(ProviderKind.TeraBox), official, ready);
        Assert.Equal(ImplementationStatus.Blocked, tera);
        Assert.Contains("no 'terabox' backend", teraNote);

        var noSmb = Catalog("drive");
        Assert.Equal(ImplementationStatus.Blocked, ProviderRegistry.Effective(ProviderRegistry.Get(ProviderKind.Smb), noSmb, ready).Status);

        // A build with a terabox backend makes TeraBox Experimental at most, never Supported.
        var fork = Catalog("drive", "terabox");
        Assert.Equal(ImplementationStatus.Experimental, ProviderRegistry.Effective(ProviderRegistry.Get(ProviderKind.TeraBox), fork, ready).Status);
        foreach (var d in ProviderRegistry.All.Where(d => d.UsesRclone))
        {
            Assert.NotEqual(ImplementationStatus.Supported, ProviderRegistry.Effective(d, fork, ready).Status);
        }
    }

    [Fact]
    public void ParseSums_ReadsClearSignedChecksumFile()
    {
        var text = "-----BEGIN PGP SIGNED MESSAGE-----\r\nHash: SHA1\r\n\r\n" +
                   "1111111111111111111111111111111111111111111111111111111111111111  rclone-v1.71.2-linux-amd64.zip\r\n" +
                   "ABCDEFabcdef0000000000000000000000000000000000000000000000000000  rclone-v1.71.2-windows-amd64.zip\r\n" +
                   "-----BEGIN PGP SIGNATURE-----\r\nxyz\r\n-----END PGP SIGNATURE-----\r\n";
        var sums = RcloneInstaller.ParseSums(text);
        Assert.Equal(2, sums.Count);
        Assert.Equal("abcdefabcdef0000000000000000000000000000000000000000000000000000", sums["rclone-v1.71.2-windows-amd64.zip"]);
    }

    private sealed class FakeDownloads : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            return Task.FromResult(Files.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (FakeDownloads Handler, string ZipName) FakeRelease(bool corruptSum)
    {
        var handler = new FakeDownloads();
        var zipName = $"rclone-v1.71.2-{RcloneInstaller.PlatformSuffix}.zip";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"rclone-v1.71.2-{RcloneInstaller.PlatformSuffix}/rclone.exe");
            using var w = entry.Open();
            w.Write(Encoding.UTF8.GetBytes("fake"));
        }

        var bytes = ms.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (corruptSum)
        {
            hash = new string('0', 64);
        }

        handler.Files["/version.txt"] = Encoding.UTF8.GetBytes("rclone v1.71.2\n");
        handler.Files["/v1.71.2/SHA256SUMS"] = Encoding.UTF8.GetBytes($"-----BEGIN PGP SIGNED MESSAGE-----\n\n{hash}  {zipName}\n");
        handler.Files["/v1.71.2/" + zipName] = bytes;
        return (handler, zipName);
    }

    [Fact]
    public async Task Installer_RejectsArchiveWithWrongChecksum_AndLeavesNothingBehind()
    {
        var (handler, zipName) = FakeRelease(corruptSum: true);
        using var http = new HttpClient(handler);
        var installer = new RcloneInstaller(http, new Uri("https://downloads.example.test/"));
        var staging = Path.Combine(_root, "staging");

        var ex = await Assert.ThrowsAsync<CloudProviderException>(() => installer.DownloadAndVerifyAsync(staging, null, default));
        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.Contains("/v1.71.2/" + zipName, handler.Requested);
        Assert.Empty(Directory.GetFileSystemEntries(staging));
    }

    [Fact]
    public async Task Installer_RejectsBinaryThatDoesNotRun()
    {
        var (handler, _) = FakeRelease(corruptSum: false);
        using var http = new HttpClient(handler);
        var installer = new RcloneInstaller(http, new Uri("https://downloads.example.test/"));
        var staging = Path.Combine(_root, "staging");

        // Checksum matches, but the extracted "rclone.exe" is not a program: it must not be installed.
        var ex = await Assert.ThrowsAsync<CloudProviderException>(() => installer.DownloadAndVerifyAsync(staging, null, default));
        Assert.Contains("does not run correctly", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(staging));
    }

    [Fact]
    public async Task Installer_RequiresChecksumEntryForThisPlatform()
    {
        var (handler, zipName) = FakeRelease(corruptSum: false);
        handler.Files["/v1.71.2/SHA256SUMS"] = Encoding.UTF8.GetBytes(new string('1', 64) + "  rclone-v1.71.2-other.zip\n");
        using var http = new HttpClient(handler);
        var installer = new RcloneInstaller(http, new Uri("https://downloads.example.test/"));

        var ex = await Assert.ThrowsAsync<CloudProviderException>(() => installer.DownloadAndVerifyAsync(Path.Combine(_root, "s"), null, default));
        Assert.Contains(zipName, ex.Message);
        Assert.DoesNotContain("/v1.71.2/" + zipName, handler.Requested);
    }
}
