using System.Collections.Concurrent;
using System.Diagnostics;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using Fsp;
using Microsoft.Win32;

namespace CloudSBiian.VirtualDrive;

public sealed record WinFspStatus(bool Installed, string? Version, string? InstallDirectory, string Message);

public sealed class MountedDrive
{
    internal MountedDrive(string letter, DriveMountSettings settings, FileSystemHost host, DriveCore core)
    {
        Letter = letter;
        Settings = settings;
        Host = host;
        Core = core;
        MountedAt = DateTimeOffset.Now;
    }

    public string Letter { get; }

    public string Root => Letter + ":\\";

    public DriveMountSettings Settings { get; }

    internal FileSystemHost Host { get; }

    public DriveCore Core { get; }

    public DateTimeOffset MountedAt { get; }
}

/// <summary>Mounts and unmounts CloudSBiian drives through WinFsp.</summary>
public sealed class DriveManager : IDisposable
{
    private readonly DriveServices _services;
    private readonly ConcurrentDictionary<string, MountedDrive> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public DriveManager(DriveServices services) => _services = services;

    public event EventHandler? Changed;

    public IReadOnlyList<MountedDrive> Mounted => _mounted.Values.OrderBy(d => d.Letter).ToList();

    public MountedDrive? Get(string letter) => _mounted.TryGetValue(Normalize(letter), out var d) ? d : null;

    /// <summary>Checks the documented WinFsp registry key and the native DLL.</summary>
    public static WinFspStatus CheckWinFsp()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            var dir = key?.GetValue("InstallDir") as string;
            if (string.IsNullOrEmpty(dir))
            {
                return new WinFspStatus(false, null, null, "WinFsp is not installed. Install it from https://winfsp.dev/rel/ (or with the CloudSBiian installer) to mount drives.");
            }

            var dll = Path.Combine(dir, "bin", Environment.Is64BitProcess ? "winfsp-x64.dll" : "winfsp-x86.dll");
            if (!File.Exists(dll))
            {
                return new WinFspStatus(false, null, dir, $"WinFsp seems incomplete: {dll} is missing. Reinstall WinFsp.");
            }

            var version = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
            return new WinFspStatus(true, version, dir, $"WinFsp {version} is installed.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new WinFspStatus(false, null, null, $"Could not check WinFsp: {ex.Message}");
        }
    }

    /// <summary>Drive letters not used by any existing volume (A and B excluded).</summary>
    public static IReadOnlyList<string> AvailableLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[..1].ToUpperInvariant()).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(c => ((char)c).ToString())
            .Where(l => !used.Contains(l))
            .ToList();
    }

    public static bool IsLetterInUse(string letter) =>
        DriveInfo.GetDrives().Any(d => d.Name.StartsWith(Normalize(letter) + ":", StringComparison.OrdinalIgnoreCase));

    public MountedDrive Mount(DriveMountSettings settings)
    {
        var letter = Normalize(settings.DriveLetter);
        lock (_gate)
        {
            if (_mounted.ContainsKey(letter))
            {
                throw new InvalidOperationException($"Drive {letter}: is already mounted by CloudSBiian.");
            }

            var winfsp = CheckWinFsp();
            if (!winfsp.Installed)
            {
                throw new InvalidOperationException(winfsp.Message);
            }

            if (IsLetterInUse(letter))
            {
                throw new InvalidOperationException($"Drive letter {letter}: is already in use. Choose another letter.");
            }

            var core = new DriveCore(_services, settings);
            var fs = new CloudFileSystem(core, string.IsNullOrWhiteSpace(settings.VolumeLabel) ? "CloudSBiian" : settings.VolumeLabel, _services.Log);
            var host = new FileSystemHost(fs);
            if (settings.NetworkDrive)
            {
                // A UNC-style prefix makes Windows show the volume as a network drive.
                host.Prefix = $@"\cloudsbiian\{letter.ToLowerInvariant()}";
            }

            var status = host.Mount(letter + ":", null, false, 0);
            if (status < 0)
            {
                host.Dispose();
                core.Detach();
                throw new InvalidOperationException($"WinFsp could not mount {letter}: (NTSTATUS 0x{status:X8}).");
            }

            var drive = new MountedDrive(letter, settings, host, core);
            _mounted[letter] = drive;
            _services.Log.Info("drive", $"Mounted {letter}: ({(core.SingleAccount ? "single account" : $"{core.AccountFolders.Count} account folder(s)")}).");
            Changed?.Invoke(this, EventArgs.Empty);
            return drive;
        }
    }

    public void Unmount(string letter)
    {
        var l = Normalize(letter);
        lock (_gate)
        {
            if (!_mounted.TryRemove(l, out var drive))
            {
                return;
            }

            try
            {
                drive.Host.Unmount();
            }
            finally
            {
                drive.Host.Dispose();
                drive.Core.Detach();
            }

            _services.Log.Info("drive", $"Unmounted {l}: (open handles at unmount: {drive.Core.OpenHandles}).");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UnmountAll()
    {
        foreach (var d in Mounted)
        {
            try
            {
                Unmount(d.Letter);
            }
            catch (Exception ex)
            {
                _services.Log.Error("drive", $"Unmount {d.Letter}: failed.", ex);
            }
        }
    }

    /// <summary>Full Windows path for an account path on the first mounted drive that exposes the account.</summary>
    public string? ResolveWindowsPath(string accountId, string cloudPath)
    {
        foreach (var d in Mounted)
        {
            var rel = d.Core.ToWindowsPath(accountId, cloudPath);
            if (rel != null)
            {
                return d.Letter + ":" + (rel.Length == 0 ? "\\" : rel);
            }
        }

        return null;
    }

    private static string Normalize(string letter)
    {
        var l = letter.Trim().TrimEnd(':', '\\').ToUpperInvariant();
        if (l.Length != 1 || l[0] < 'A' || l[0] > 'Z')
        {
            throw new ArgumentException($"Invalid drive letter '{letter}'.");
        }

        return l;
    }

    public void Dispose() => UnmountAll();
}
