using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Security;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record LogRow(string Time, string Level, string Category, string Account, string Message);

public sealed class LogsViewModel : PageViewModel
{
    private readonly AppHost _host;
    private LogLevel _minimum = LogLevel.Info;
    private bool _history;
    private string _search = string.Empty;
    private int _tick;

    public LogsViewModel(AppHost host)
        : base("Logs & Diagnostics", "")
    {
        _host = host;
        Reload = new RelayCommand(Load);
        OpenFolder = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", _host.Paths.LogDirectory) { UseShellExecute = true }));
        Export = new AsyncCommand(ExportAsync);
        Check = new AsyncCommand(async () => TextReportDialog.Show("Dependency check", await BuildReportAsync()));
    }

    public LogLevel[] Levels { get; } = Enum.GetValues<LogLevel>();

    public ObservableCollection<LogRow> Entries { get; } = new();

    public LogLevel Minimum
    {
        get => _minimum;
        set
        {
            if (SetProperty(ref _minimum, value))
            {
                Load();
            }
        }
    }

    public bool History
    {
        get => _history;
        set
        {
            if (SetProperty(ref _history, value))
            {
                Load();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                Load();
            }
        }
    }

    public RelayCommand Reload { get; }

    public RelayCommand OpenFolder { get; }

    public AsyncCommand Export { get; }

    public AsyncCommand Check { get; }

    public override void OnActivated()
    {
        Load();
        base.OnActivated();
    }

    public override void Refresh()
    {
        if (!History && _tick++ % 3 == 0)
        {
            Load();
        }
    }

    private void Load()
    {
        IEnumerable<LogEntry> source = History
            ? _host.ActivityLog.Query(Minimum, null, 1000)
            : _host.FileLog.Recent.Where(e => e.Level >= Minimum).Reverse().Take(1000);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            source = source.Where(e => e.Message.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(Search, StringComparison.OrdinalIgnoreCase));
        }

        Entries.Clear();
        foreach (var e in source)
        {
            var account = e.AccountId == null ? string.Empty : _host.Accounts.Get(e.AccountId)?.Account.DisplayName ?? e.AccountId;
            Entries.Add(new LogRow(e.Timestamp.ToString("MM-dd HH:mm:ss"), e.Level.ToString(), e.Category, account, e.Message));
        }
    }

    private async Task<string> BuildReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CloudSBiian {typeof(App).Assembly.GetName().Version}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, {Environment.ProcessorCount} logical CPUs");
        var winfsp = DriveManager.CheckWinFsp();
        sb.AppendLine($"WinFsp: {(winfsp.Installed ? "OK" : "MISSING")} — {winfsp.Message}");
        var rclone = RcloneLocator.Find(_host.Settings.Current.RclonePath);
        sb.AppendLine($"rclone: {(rclone == null ? "MISSING (rclone-based providers blocked)" : $"{rclone} — {await RcloneLocator.GetVersionAsync(rclone, CancellationToken.None) ?? "version unknown"}")}");
        var catalog = _host.Accounts.Catalog;
        if (catalog != null)
        {
            sb.AppendLine($"rclone backends: {catalog.Backends.Count} (terabox: {(catalog.Has("terabox") ? "yes" : "no")})");
        }

        sb.AppendLine($"Data folder: {_host.Paths.DataRoot}{(_host.Paths.IsPortable ? " (portable)" : string.Empty)}");
        sb.AppendLine($"Cache folder: {_host.Cache.CacheDirectory} — free disk {Format.Bytes(_host.Cache.DiskFreeBytes())}");
        sb.AppendLine($"Cache usage: {Format.Bytes(_host.Cache.Store.TotalPhysicalBytes)} of {Format.Bytes(_host.Settings.Current.CacheBudgetBytes)}; staging {Format.Bytes(_host.Staging.TotalBytes())}");
        sb.AppendLine($"Mounted drives: {string.Join(", ", _host.Drives.Mounted.Select(d => d.Letter + ":"))}");
        sb.AppendLine($"Pending uploads: {_host.Uploads.CountPending()}");
        sb.AppendLine();
        sb.AppendLine("Accounts (identities masked, no secrets):");
        foreach (var r in _host.Accounts.Runtimes)
        {
            sb.AppendLine($"  {r.Account.DisplayName} [{r.Account.Kind}, {r.Id}] {r.State}{(r.LastError == null ? string.Empty : " — " + r.LastError)}");
        }

        return SecretRedactor.Redact(sb.ToString());
    }

    private async Task ExportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"CloudSBiian-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip archive|*.zip",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var report = await BuildReportAsync();
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create);
            Write(zip, "report.txt", report);
            if (File.Exists(_host.Paths.SettingsPath))
            {
                Write(zip, "settings.json", SecretRedactor.Redact(File.ReadAllText(_host.Paths.SettingsPath)));
            }

            var activity = new StringBuilder();
            foreach (var e in _host.ActivityLog.Query(LogLevel.Info, null, 5000))
            {
                activity.AppendLine($"{e.Timestamp:O} [{e.Level}] {e.Category} {e.AccountId}: {e.Message}");
            }

            Write(zip, "activity.txt", activity.ToString());
            foreach (var log in Directory.GetFiles(_host.Paths.LogDirectory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                Write(zip, "logs/" + Path.GetFileName(log), SecretRedactor.Redact(reader.ReadToEnd()));
            }
        });
        Status = $"Diagnostics exported to {dlg.FileName}. It contains no passwords or tokens; review it before sharing.";
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(text);
    }
}
