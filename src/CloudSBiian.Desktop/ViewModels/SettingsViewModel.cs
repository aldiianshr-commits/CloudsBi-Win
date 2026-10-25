using System.Diagnostics;
using System.Globalization;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using Microsoft.Win32;

namespace CloudSBiian.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly AppHost _host;
    private string _rclonePath = string.Empty;
    private string _rcloneInfo = string.Empty;
    private AppTheme _theme;
    private string _metadataTtl = "30";
    private string _concurrency = "4";
    private string _uploadAttempts = "8";
    private string _unknownQuotaGb = "1024";
    private string _fullLocalDir = string.Empty;
    private string _logLevel = "Info";
    private bool _startWithWindows;

    public SettingsViewModel(AppHost host)
        : base("Settings", "")
    {
        _host = host;
        BrowseRclone = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog { Filter = "rclone.exe|rclone.exe|Executables (*.exe)|*.exe", Title = "Select rclone.exe" };
            if (dlg.ShowDialog() == true)
            {
                RclonePath = dlg.FileName;
            }
        });
        DetectRclone = new AsyncCommand(DetectAsync);
        BrowseFullLocal = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog { Title = "Default folder for Full Local installs" };
            if (dlg.ShowDialog() == true)
            {
                FullLocalDirectory = dlg.FolderName;
            }
        });
        OpenDataFolder = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", _host.Paths.DataRoot) { UseShellExecute = true }));
        Save = new AsyncCommand(SaveAsync);
        Load();
    }

    public AppTheme[] Themes { get; } = Enum.GetValues<AppTheme>();

    public string[] LogLevels { get; } = ["Debug", "Info", "Warning", "Error"];

    public string RclonePath { get => _rclonePath; set => SetProperty(ref _rclonePath, value); }

    public string RcloneInfo { get => _rcloneInfo; private set => SetProperty(ref _rcloneInfo, value); }

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value))
            {
                App.ApplyTheme(value);
            }
        }
    }

    public string MetadataTtl { get => _metadataTtl; set => SetProperty(ref _metadataTtl, value); }

    public string Concurrency { get => _concurrency; set => SetProperty(ref _concurrency, value); }

    public string UploadAttempts { get => _uploadAttempts; set => SetProperty(ref _uploadAttempts, value); }

    public string UnknownQuotaGb { get => _unknownQuotaGb; set => SetProperty(ref _unknownQuotaGb, value); }

    public string FullLocalDirectory { get => _fullLocalDir; set => SetProperty(ref _fullLocalDir, value); }

    public string LogLevel { get => _logLevel; set => SetProperty(ref _logLevel, value); }

    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }

    public string DataFolder => _host.Paths.DataRoot + (_host.Paths.IsPortable ? "  (portable mode)" : string.Empty);

    public RelayCommand BrowseRclone { get; }

    public AsyncCommand DetectRclone { get; }

    public RelayCommand BrowseFullLocal { get; }

    public RelayCommand OpenDataFolder { get; }

    public AsyncCommand Save { get; }

    public override void OnActivated()
    {
        Load();
        _ = DetectAsync();
        base.OnActivated();
    }

    private void Load()
    {
        var s = _host.Settings.Current;
        RclonePath = s.RclonePath ?? string.Empty;
        _theme = s.Theme;
        OnPropertyChanged(nameof(Theme));
        MetadataTtl = s.MetadataTtlSeconds.ToString(CultureInfo.InvariantCulture);
        Concurrency = s.MaxConcurrentDownloadsPerAccount.ToString(CultureInfo.InvariantCulture);
        UploadAttempts = s.MaxUploadAttempts.ToString(CultureInfo.InvariantCulture);
        UnknownQuotaGb = (s.UnknownQuotaReportedBytes / AppSettings.GiB).ToString(CultureInfo.InvariantCulture);
        FullLocalDirectory = s.DefaultFullLocalDirectory ?? string.Empty;
        LogLevel = s.LogLevel;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        StartWithWindows = key?.GetValue("CloudSBiian") != null;
    }

    private async Task DetectAsync()
    {
        var path = RcloneLocator.Find(string.IsNullOrWhiteSpace(RclonePath) ? null : RclonePath);
        if (path == null)
        {
            RcloneInfo = string.IsNullOrWhiteSpace(RclonePath)
                ? "rclone not found (not bundled, not on PATH). Google Drive, OneDrive, Dropbox, SFTP, S3, SMB and TeraBox need it."
                : "The selected file does not exist.";
            return;
        }

        var version = await RcloneLocator.GetVersionAsync(path, CancellationToken.None);
        var catalog = _host.Accounts.Catalog;
        var tera = catalog == null ? "unknown until rclone is started" : catalog.Has("terabox") ? "available (unofficial backend)" : "not available in this build";
        RcloneInfo = $"{path} — {version ?? "version unknown"} · TeraBox backend: {tera}";
    }

    private static int ParseInt(string text, string field, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new FormatException($"{field} must be a whole number between {min} and {max}.");
        }

        return v;
    }

    private async Task SaveAsync()
    {
        var previous = _host.Settings.Current.Clone();
        var s = _host.Settings.Current.Clone();
        s.RclonePath = string.IsNullOrWhiteSpace(RclonePath) ? null : RclonePath.Trim();
        s.Theme = Theme;
        s.MetadataTtlSeconds = ParseInt(MetadataTtl, "Metadata cache time", 1, 3600);
        s.MaxConcurrentDownloadsPerAccount = ParseInt(Concurrency, "Parallel downloads per account", 1, 32);
        s.MaxUploadAttempts = ParseInt(UploadAttempts, "Upload attempts", 1, 100);
        s.UnknownQuotaReportedBytes = ParseInt(UnknownQuotaGb, "Reported capacity", 1, 1_000_000) * AppSettings.GiB;
        s.DefaultFullLocalDirectory = string.IsNullOrWhiteSpace(FullLocalDirectory) ? null : FullLocalDirectory.Trim();
        s.LogLevel = LogLevel;
        var errors = s.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errors));
        }

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartWithWindows)
            {
                key.SetValue("CloudSBiian", $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue("CloudSBiian", throwOnMissingValue: false);
            }
        }

        Status = "Saving…";
        await _host.ApplySettingsAsync(s, previous);
        await DetectAsync();
        Status = "Settings saved.";
    }
}
