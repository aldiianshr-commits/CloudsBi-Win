using System.Diagnostics;

namespace CloudSBiian.CloudProviders.Rclone;

/// <summary>Finds the rclone executable: explicit setting, bundled copy, then PATH.</summary>
public static class RcloneLocator
{
    public static string? Find(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var exeName = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var bundled = Path.Combine(AppContext.BaseDirectory, "rclone", exeName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        bundled = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
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

            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return output.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
