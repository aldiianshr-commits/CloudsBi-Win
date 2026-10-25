namespace CloudSBiian.GameLauncher;

public enum CheckSeverity
{
    Ok,
    Info,
    Warning,
    Error,
}

public sealed record CheckResult(string Name, CheckSeverity Severity, string Message, string? Remedy = null);

/// <summary>
/// Looks for well-known file names that indicate launcher, DRM or anti-cheat requirements.
/// These are hints, not guarantees: absence of a marker does not prove compatibility.
/// </summary>
public static class CompatibilityScanner
{
    private sealed record Marker(string Pattern, bool IsFolder, CheckSeverity Severity, string Message, string Remedy);

    private static readonly Marker[] Markers =
    [
        new("EasyAntiCheat", true, CheckSeverity.Warning,
            "Easy Anti-Cheat detected. Anti-cheat drivers often refuse game files on virtual or network volumes.",
            "Use Full Local mode."),
        new("BattlEye", true, CheckSeverity.Warning,
            "BattlEye detected. Anti-cheat services commonly require game files on a local NTFS disk.",
            "Use Full Local mode."),
        new("EAAntiCheat", true, CheckSeverity.Warning,
            "EA Anti-Cheat detected; it typically needs a local installation managed by the EA app.",
            "Install the game locally through the EA app."),
        new("steam_api64.dll", false, CheckSeverity.Info,
            "Steamworks DLL found: the Steam client usually must be running and signed in to the owning account.",
            "Start Steam before launching."),
        new("steam_api.dll", false, CheckSeverity.Info,
            "Steamworks DLL found: the Steam client usually must be running and signed in to the owning account.",
            "Start Steam before launching."),
        new("EOSSDK-Win64-Shipping.dll", false, CheckSeverity.Info,
            "Epic Online Services SDK found: online features may require the Epic Games Launcher or an Epic account.",
            "Start the Epic Games Launcher if the game asks for it."),
        new("uplay_r1_loader64.dll", false, CheckSeverity.Info,
            "Ubisoft Connect loader found: the Ubisoft Connect client is usually required.",
            "Start Ubisoft Connect before launching."),
        new("upc_r2_loader64.dll", false, CheckSeverity.Info,
            "Ubisoft Connect loader found: the Ubisoft Connect client is usually required.",
            "Start Ubisoft Connect before launching."),
        new("Activation64.dll", false, CheckSeverity.Info,
            "Origin/EA activation component found: the EA app may be required.",
            "Start the EA app before launching."),
        new("vc_redist.x64.exe", false, CheckSeverity.Info,
            "A Visual C++ redistributable installer is bundled; the game may need it installed once.",
            "Run the bundled redistributable installer if the game fails to start."),
        new("DXSETUP.exe", false, CheckSeverity.Info,
            "A DirectX setup is bundled; the game may need legacy DirectX components.",
            "Run the bundled DirectX setup if the game fails to start."),
    ];

    /// <summary>Scans names (relative paths with "/" separators) found in the game folder.</summary>
    public static IReadOnlyList<CheckResult> Scan(IEnumerable<(string RelativePath, bool IsDirectory)> items)
    {
        var results = new List<CheckResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, isDir) in items)
        {
            var name = rel.Split('/').Last();
            foreach (var m in Markers)
            {
                if (m.IsFolder == isDir && name.Equals(m.Pattern, StringComparison.OrdinalIgnoreCase) && seen.Add(m.Message))
                {
                    results.Add(new CheckResult("Compatibility", m.Severity, m.Message + $" ({rel})", m.Remedy));
                }
            }
        }

        if (results.Count == 0)
        {
            results.Add(new CheckResult("Compatibility", CheckSeverity.Info,
                "No known launcher/anti-cheat markers found. This does not guarantee the game runs from a virtual drive.", null));
        }

        return results;
    }
}
