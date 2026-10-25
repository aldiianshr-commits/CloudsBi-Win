using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudSBiian.Core.Configuration;

public enum WriteMode
{
    /// <summary>Changes are kept in the local staging area and uploaded by the background queue.</summary>
    WriteBack,
    /// <summary>Closing a modified file waits until the upload finished (or failed into the queue).</summary>
    WriteThroughOnClose,
}

public enum PredictionAggressiveness
{
    Off,
    Conservative,
    Balanced,
    Aggressive,
}

public enum AppTheme
{
    Dark,
    Light,
}

public sealed class DriveMountSettings
{
    public string DriveLetter { get; set; } = "X";

    /// <summary>Account ids exposed on this drive. Empty = all enabled accounts (one folder per account).</summary>
    public List<string> AccountIds { get; set; } = new();

    public string VolumeLabel { get; set; } = "CloudSBiian";

    public bool NetworkDrive { get; set; }

    public bool MountOnStartup { get; set; }

    public bool ReadOnly { get; set; }
}

/// <summary>Non-secret application settings persisted as JSON.</summary>
public sealed class AppSettings
{
    public const long GiB = 1024L * 1024 * 1024;

    public long CacheBudgetBytes { get; set; } = 20 * GiB;

    public long MinFreeDiskBytes { get; set; } = 10 * GiB;

    /// <summary>Null = default location under the data directory.</summary>
    public string? CacheDirectory { get; set; }

    public int ChunkSizeMiB { get; set; } = 4;

    public bool VerifyChunksOnRead { get; set; } = true;

    public WriteMode WriteMode { get; set; } = WriteMode.WriteBack;

    public int MetadataTtlSeconds { get; set; } = 30;

    public int MaxConcurrentDownloadsPerAccount { get; set; } = 4;

    public int MaxUploadAttempts { get; set; } = 8;

    public bool PredictionEnabled { get; set; } = true;

    public PredictionAggressiveness PredictionAggressiveness { get; set; } = PredictionAggressiveness.Balanced;

    /// <summary>0 = unlimited.</summary>
    public long PrefetchBandwidthLimitBytesPerSecond { get; set; } = 8L * 1024 * 1024;

    /// <summary>Capacity reported to Windows when the backend does not report a quota.</summary>
    public long UnknownQuotaReportedBytes { get; set; } = 1024 * GiB;

    public string? RclonePath { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public string LogLevel { get; set; } = "Info";

    public List<DriveMountSettings> Mounts { get; set; } = new() { new DriveMountSettings() };

    public string? DefaultFullLocalDirectory { get; set; }

    /// <summary>Optimize &amp; Prepare asks before prefetching more than this many bytes.</summary>
    public long PrepareConfirmThresholdBytes { get; set; } = 2 * GiB;

    [JsonIgnore]
    public int ChunkSizeBytes => Math.Clamp(ChunkSizeMiB, 1, 64) * 1024 * 1024;

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    public IEnumerable<string> Validate()
    {
        if (CacheBudgetBytes < 512L * 1024 * 1024)
        {
            yield return "Cache budget must be at least 512 MB.";
        }

        if (MinFreeDiskBytes < 0)
        {
            yield return "Minimum free disk space cannot be negative.";
        }

        if (ChunkSizeMiB is < 1 or > 64)
        {
            yield return "Chunk size must be between 1 and 64 MiB.";
        }

        if (MaxConcurrentDownloadsPerAccount is < 1 or > 32)
        {
            yield return "Concurrent downloads per account must be between 1 and 32.";
        }

        var letters = Mounts.Select(m => m.DriveLetter.ToUpperInvariant()).ToList();
        if (letters.Distinct().Count() != letters.Count)
        {
            yield return "Two drives use the same drive letter.";
        }
    }
}

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            Current = settings;
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
            // Corrupt settings: keep a copy for diagnostics and start with defaults.
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }

        return new AppSettings();
    }
}
