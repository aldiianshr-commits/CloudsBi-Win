using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;

namespace CloudSBiian.CloudProviders.Rclone;

public sealed record RcloneDownloadProgress(string Stage, long BytesDone, long? BytesTotal);

/// <summary>A downloaded, hash-verified rclone executable waiting in a staging folder.</summary>
public sealed record RcloneStagedInstall(string StagingDirectory, string ExecutablePath, string Version, string ArchiveName, string Sha256);

/// <summary>
/// Downloads the official rclone release for Windows from downloads.rclone.org (the download
/// location published by the rclone project), checks the archive against the release's
/// SHA256SUMS file, extracts rclone.exe and checks that it runs and reports the expected
/// version. Nothing else is downloaded and no credentials are sent. rclone is MIT licensed;
/// its README (with the license section) is kept next to the executable.
/// Limitation: the PGP signature of SHA256SUMS is not verified (both files come from the same
/// HTTPS origin), which is stated in SECURITY.md.
/// </summary>
public sealed partial class RcloneInstaller
{
    public static readonly Uri DefaultBaseUri = new("https://downloads.rclone.org/");
    private const long MaxArchiveBytes = 250L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly IAppLog _log;

    public RcloneInstaller(HttpClient http, Uri? baseUri = null, IAppLog? log = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
        _log = log ?? NullLog.Instance;
    }

    /// <summary>"windows-amd64" / "windows-arm64" (rclone's naming); other OSes for tests on Linux.</summary>
    public static string PlatformSuffix
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "386",
                _ => "amd64",
            };
            return $"{os}-{arch}";
        }
    }

    [GeneratedRegex(@"^rclone\s+(v\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex VersionFileRegex();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SumLineRegex();

    /// <summary>Reads the current stable version from version.txt ("rclone v1.71.2").</summary>
    public async Task<string> GetLatestVersionAsync(CancellationToken ct)
    {
        var text = await GetStringAsync(new Uri(_baseUri, "version.txt"), ct).ConfigureAwait(false);
        var m = VersionFileRegex().Match(text);
        if (!m.Success)
        {
            throw new CloudProviderException(CloudErrorKind.Unknown, $"Unexpected content in {_baseUri}version.txt.");
        }

        return m.Groups[1].Value;
    }

    /// <summary>Parses a (possibly PGP clear-signed) SHA256SUMS file.</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in SumLineRegex().Matches(text.Replace("\r", string.Empty)))
        {
            map[m.Groups[2].Value] = m.Groups[1].Value.ToLowerInvariant();
        }

        return map;
    }

    /// <summary>Downloads and verifies rclone into a new folder under <paramref name="stagingRoot"/>.</summary>
    public async Task<RcloneStagedInstall> DownloadAndVerifyAsync(string stagingRoot, IProgress<RcloneDownloadProgress>? progress, CancellationToken ct, string? version = null)
    {
        progress?.Report(new RcloneDownloadProgress("Checking latest version", 0, null));
        version ??= await GetLatestVersionAsync(ct).ConfigureAwait(false);
        var archive = $"rclone-{version}-{PlatformSuffix}.zip";
        var folder = new Uri(_baseUri, version + "/");

        progress?.Report(new RcloneDownloadProgress("Downloading checksums", 0, null));
        var sums = ParseSums(await GetStringAsync(new Uri(folder, "SHA256SUMS"), ct).ConfigureAwait(false));
        if (!sums.TryGetValue(archive, out var expected))
        {
            throw new CloudProviderException(CloudErrorKind.Unknown, $"SHA256SUMS of rclone {version} has no entry for {archive}.");
        }

        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, "rclone-download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, archive);
        try
        {
            var actual = await DownloadFileAsync(new Uri(folder, archive), zipPath, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new CloudProviderException(CloudErrorKind.Unknown, $"Checksum mismatch for {archive}: expected {expected}, got {actual}. The file was discarded.");
            }

            _log.Info("rclone", $"Downloaded {archive} ({new FileInfo(zipPath).Length} bytes), SHA-256 verified.");
            progress?.Report(new RcloneDownloadProgress("Extracting", 0, null));
            var exeName = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
            var exePath = Path.Combine(staging, exeName);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CloudProviderException(CloudErrorKind.Unknown, $"{archive} does not contain {exeName}.");
                exeEntry.ExtractToFile(exePath, overwrite: true);
                var readme = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "README.txt", StringComparison.OrdinalIgnoreCase));
                readme?.ExtractToFile(Path.Combine(staging, "README.txt"), overwrite: true);
            }

            File.Delete(zipPath);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(exePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            progress?.Report(new RcloneDownloadProgress("Validating", 0, null));
            var reported = await RcloneLocator.GetVersionAsync(exePath, ct).ConfigureAwait(false);
            var parsed = RcloneLocator.ParseVersion(reported);
            if (parsed == null || reported == null || !reported.Contains(version, StringComparison.Ordinal))
            {
                throw new CloudProviderException(CloudErrorKind.DependencyMissing, $"The downloaded rclone does not run correctly (reported: {reported ?? "nothing"}).");
            }

            return new RcloneStagedInstall(staging, exePath, version, archive, actual);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Moves a staged install into <paramref name="targetDirectory"/> (replacing an older copy).</summary>
    public static string Commit(RcloneStagedInstall staged, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, Path.GetFileName(staged.ExecutablePath));
        File.Move(staged.ExecutablePath, target, overwrite: true);
        var readme = Path.Combine(staged.StagingDirectory, "README.txt");
        if (File.Exists(readme))
        {
            File.Move(readme, Path.Combine(targetDirectory, "README.txt"), overwrite: true);
        }

        File.WriteAllText(Path.Combine(targetDirectory, "SOURCE.txt"),
            $"rclone {staged.Version} ({staged.ArchiveName}) downloaded by CloudSBiian from https://downloads.rclone.org/ on {DateTimeOffset.Now:O}.{Environment.NewLine}" +
            $"SHA-256 of the archive (matched the release SHA256SUMS): {staged.Sha256}{Environment.NewLine}" +
            $"rclone is licensed under the MIT license: https://github.com/rclone/rclone/blob/master/COPYING{Environment.NewLine}");
        TryDelete(staged.StagingDirectory);
        return target;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CloudProviderException(CloudErrorKind.Unknown, $"{uri} returned HTTP {(int)response.StatusCode}.");
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudProviderException(CloudErrorKind.Network, $"Could not reach {uri.Host}: {ex.Message}", ex);
        }
    }

    private async Task<string> DownloadFileAsync(Uri uri, string path, IProgress<RcloneDownloadProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new CloudProviderException(CloudErrorKind.Unknown, $"{uri} returned HTTP {(int)response.StatusCode}.");
            }

            var total = response.Content.Headers.ContentLength;
            if (total > MaxArchiveBytes)
            {
                throw new CloudProviderException(CloudErrorKind.Unknown, $"{uri} is unexpectedly large ({total} bytes).");
            }

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > MaxArchiveBytes)
                {
                    throw new CloudProviderException(CloudErrorKind.Unknown, $"{uri} is unexpectedly large.");
                }

                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = DateTime.UtcNow;
                    progress?.Report(new RcloneDownloadProgress("Downloading rclone", done, total));
                }
            }

            progress?.Report(new RcloneDownloadProgress("Downloading rclone", done, total));
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (HttpRequestException ex)
        {
            throw new CloudProviderException(CloudErrorKind.Network, $"Download from {uri.Host} failed: {ex.Message}", ex);
        }
    }
}
