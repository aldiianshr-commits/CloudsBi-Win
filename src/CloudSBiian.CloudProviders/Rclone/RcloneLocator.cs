using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudSBiian.CloudProviders.Rclone;

/// <summary>Where a candidate rclone executable was looked for and whether it existed.</summary>
public sealed record RcloneCandidate(string Path, string Source, bool Exists);

public sealed record RcloneLocation(string? Path, string? Source, IReadOnlyList<RcloneCandidate> Searched, string? ConfiguredPathProblem);

/// <summary>
/// Finds rclone.exe without relying only on the PATH of the running process. Search order:
/// 1. path chosen in Settings, 2. copy bundled with this CloudSBiian (next to the executable,
/// also for single-file builds), 3. copy installed by CloudSBiian's "Download rclone" button,
/// 4. rclone bundled with an installed CloudSBiian (Program Files), 5. common package managers
/// (winget, Scoop, Chocolatey) and "Program Files\rclone", 6. PATH (current process and the
/// up-to-date user/machine PATH from the registry, so a fresh install is found without re-login).
/// </summary>
public static partial class RcloneLocator
{
    /// <summary>Oldest rclone release whose rc API (config/create nonInteractive, rc-serve, job/*) CloudSBiian relies on.</summary>
    public static readonly Version MinimumVersion = new(1, 60, 0);

    /// <summary>Inno Setup uninstall key of CloudSBiian (AppId in installer/CloudSBiian.iss).</summary>
    private const string InstallerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6C1C2B0E-5E4A-4E8B-9F57-0B6C1A7E5D21}_is1";

    private static string ExeName => OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

    /// <summary>Directory used by <see cref="RcloneInstaller"/> for the copy CloudSBiian downloads itself.</summary>
    public static string? ManagedDirectory { get; set; }

    public static string? Find(string? configuredPath) => Locate(configuredPath).Path;

    /// <summary>
    /// Checks every known location. <see cref="RcloneLocation.Path"/> is the first existing one;
    /// <see cref="RcloneLocation.Searched"/> lists all locations with their existence flag.
    /// A path chosen in Settings that does not exist is reported, and the search continues.
    /// </summary>
    public static RcloneLocation Locate(string? configuredPath)
    {
        var searched = new List<RcloneCandidate>();
        string? problem = null;

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }

            if (searched.Any(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            searched.Add(new RcloneCandidate(full, source, File.Exists(full)));
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim().Trim('"');
            if (Directory.Exists(configured))
            {
                configured = System.IO.Path.Combine(configured, ExeName);
            }

            Add(configured, SettingsSource);
            if (searched.Count == 0 || !searched[0].Exists)
            {
                problem = $"The rclone path set in Settings does not exist: {configuredPath}";
            }
        }

        foreach (var (path, source) in Candidates())
        {
            Add(path, source);
        }

        var hit = searched.FirstOrDefault(c => c.Exists);
        return new RcloneLocation(hit?.Path, hit?.Source, searched, problem);
    }

    public const string SettingsSource = "Settings";

    /// <summary>Runs "rclone version" and checks it against <see cref="MinimumVersion"/>. Error is null when usable.</summary>
    public static async Task<(Version? Version, string? VersionLine, string? Error)> ValidateAsync(string exePath, CancellationToken ct)
    {
        if (!File.Exists(exePath))
        {
            return (null, null, $"File not found: {exePath}");
        }

        var line = await GetVersionAsync(exePath, ct).ConfigureAwait(false);
        var version = ParseVersion(line);
        if (version == null)
        {
            return (null, line, $"{exePath} did not report an rclone version (\"rclone version\" failed or this is not rclone).");
        }

        if (version < MinimumVersion)
        {
            return (version, line, $"{line} at {exePath} is too old; CloudSBiian needs v{MinimumVersion.ToString(3)} or newer.");
        }

        return (version, line, null);
    }

    private static IEnumerable<(string? Path, string Source)> Candidates()
    {
        var exe = ExeName;
        var baseDir = AppContext.BaseDirectory;
        yield return (System.IO.Path.Combine(baseDir, "rclone", exe), "bundled with CloudSBiian");
        yield return (System.IO.Path.Combine(baseDir, exe), "next to CloudSBiian");

        // Single-file builds extract to a temp folder; the folder of the .exe itself matters too.
        var processDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        if (!string.IsNullOrEmpty(processDir))
        {
            yield return (System.IO.Path.Combine(processDir, "rclone", exe), "next to CloudSBiian.exe");
            yield return (System.IO.Path.Combine(processDir, exe), "next to CloudSBiian.exe");
        }

        if (!string.IsNullOrEmpty(ManagedDirectory))
        {
            yield return (System.IO.Path.Combine(ManagedDirectory, exe), "downloaded by CloudSBiian");
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in PathCandidates(Environment.GetEnvironmentVariable("PATH")))
            {
                yield return (p, "PATH");
            }

            yield break;
        }

        foreach (var installDir in InstalledCloudSBiianDirectories())
        {
            yield return (System.IO.Path.Combine(installDir, "rclone", exe), "installed CloudSBiian");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (System.IO.Path.Combine(programFiles, "CloudSBiian", "rclone", exe), "installed CloudSBiian");
        yield return (System.IO.Path.Combine(programFiles, "rclone", exe), "Program Files");
        yield return (System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe), "winget");
        foreach (var p in Glob(System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), "Rclone.Rclone*", exe))
        {
            yield return (p, "winget");
        }

        yield return (System.IO.Path.Combine(userProfile, "scoop", "apps", "rclone", "current", exe), "Scoop");
        yield return (System.IO.Path.Combine(userProfile, "scoop", "shims", exe), "Scoop");
        foreach (var p in Glob(System.IO.Path.Combine(programData, "chocolatey", "lib", "rclone.portable", "tools"), "rclone-*", exe))
        {
            yield return (p, "Chocolatey");
        }

        yield return (System.IO.Path.Combine(programData, "chocolatey", "bin", exe), "Chocolatey");

        foreach (var p in PathCandidates(Environment.GetEnvironmentVariable("PATH")))
        {
            yield return (p, "PATH");
        }

        // PATH as stored now (the running process may have been started before rclone was installed).
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? value = null;
            try
            {
                value = Environment.GetEnvironmentVariable("PATH", target);
            }
            catch (System.Security.SecurityException)
            {
            }

            foreach (var p in PathCandidates(value))
            {
                yield return (p, "PATH (registry)");
            }
        }
    }

    private static IEnumerable<string> PathCandidates(string? pathValue)
    {
        foreach (var dir in (pathValue ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? candidate = null;
            try
            {
                candidate = System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), ExeName);
            }
            catch (ArgumentException)
            {
            }

            if (candidate != null)
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> Glob(string root, string dirPattern, string exe)
    {
        string[] dirs;
        try
        {
            dirs = Directory.Exists(root) ? Directory.GetDirectories(root, dirPattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var d in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var direct = System.IO.Path.Combine(d, exe);
            if (File.Exists(direct))
            {
                yield return direct;
            }

            string[] sub;
            try
            {
                sub = Directory.GetDirectories(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var s in sub.OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase))
            {
                yield return System.IO.Path.Combine(s, exe);
            }
        }
    }

    private static IEnumerable<string> InstalledCloudSBiianDirectories()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            {
                string? location = null;
                try
                {
                    using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(InstallerKey);
                    location = key?.GetValue("InstallLocation") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }

                if (!string.IsNullOrWhiteSpace(location))
                {
                    yield return location.TrimEnd('\\');
                }
            }
        }
    }

    [GeneratedRegex(@"rclone\s+v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    /// <summary>Parses "rclone v1.71.2" (also "-DEV"/fork suffixes) into a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        var m = VersionRegex().Match(text ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(Part(1), Part(2), Part(3));
    }

    /// <summary>Runs "rclone version" and returns the first line, or null on failure.</summary>
    public static async Task<string?> GetVersionAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("version");
            using var p = Process.Start(psi);
            if (p == null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return output.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }
}
