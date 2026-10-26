namespace CloudSBiian.Core.Configuration;

/// <summary>
/// File system locations. Installed mode stores data in %LOCALAPPDATA%\CloudSBiian.
/// Portable mode (a "portable.flag" file next to the executable) stores data in ".\data".
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SecretsDirectory);
    }

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "cloudsbiian.db");

    public string SettingsPath => Path.Combine(DataRoot, "settings.json");

    public string LogDirectory => Path.Combine(DataRoot, "logs");

    public string SecretsDirectory => Path.Combine(DataRoot, "secrets");

    public string DefaultCacheDirectory => Path.Combine(DataRoot, "cache");

    public string RuntimeDirectory => Path.Combine(DataRoot, "runtime");

    /// <summary>Helper programs CloudSBiian downloads on request (e.g. tools\rclone\rclone.exe).</summary>
    public string ToolsDirectory => Path.Combine(DataRoot, "tools");

    public bool IsPortable { get; init; }

    public static AppPaths Resolve()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.flag")))
        {
            return new AppPaths(Path.Combine(exeDir, "data")) { IsPortable = true };
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, "CloudSBiian"));
    }
}
