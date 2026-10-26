using System.Text;
using System.Windows;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Security;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using Microsoft.Win32;

namespace CloudSBiian.Desktop.ViewModels;

/// <summary>
/// rclone dependency panel shared by the Accounts and Settings pages and the Add Account dialog.
/// Shows the real result of <see cref="CloudProviders.AccountManager.CheckRcloneAsync"/> and offers
/// Download (official release, SHA-256 verified), Browse (pick rclone.exe, version-checked) and Re-check.
/// </summary>
public sealed class RcloneDependencyViewModel : ObservableObject
{
    private static RcloneDependencyViewModel? _shared;
    private readonly AppHost _host;
    private RcloneStatus _status;
    private string? _progress;
    private double _progressPercent;
    private bool _progressIndeterminate;
    private bool _busy;
    private CancellationTokenSource? _downloadCts;

    private RcloneDependencyViewModel(AppHost host)
    {
        _host = host;
        _status = host.Accounts.RcloneStatus;
        host.Accounts.RcloneStatusChanged += (_, s) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                Apply(s);
            }
            else
            {
                dispatcher.InvokeAsync(() => Apply(s));
            }
        };
        Download = new AsyncCommand(DownloadAsync, () => !_busy);
        CancelDownload = new RelayCommand(() => _downloadCts?.Cancel(), () => _downloadCts != null);
        Browse = new AsyncCommand(BrowseAsync, () => !_busy);
        Recheck = new AsyncCommand(RecheckAsync, () => !_busy);
        ShowDetails = new RelayCommand(() => TextReportDialog.Show("rclone dependency check", BuildReport()));
    }

    public static RcloneDependencyViewModel For(AppHost host) => _shared ??= new RcloneDependencyViewModel(host);

    /// <summary>Raised on the UI thread after the status changed (pages refresh their provider lists).</summary>
    public event EventHandler? StatusChanged;

    public RcloneStatus Status => _status;

    public string State => _status.State.ToString();

    public string Headline => _status.State switch
    {
        RcloneState.Ready => $"rclone {_status.Version} is ready",
        RcloneState.Missing => "rclone is not installed",
        RcloneState.Unusable => "The rclone that was found cannot be used",
        RcloneState.StartFailed => "rclone was found but could not be started",
        RcloneState.Checking => "Checking rclone…",
        _ => "rclone has not been checked yet",
    };

    public string Message => _status.Message;

    public string Location => _status.ExecutablePath == null ? "—" : $"{_status.ExecutablePath} ({_status.Source})";

    public bool NeedsAction => _status.NeedsAction;

    public bool IsReady => _status.IsReady;

    public string ManagedDirectory => RcloneLocator.ManagedDirectory ?? "—";

    public string? Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }

    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }

    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    public bool IsDownloading => _downloadCts != null;

    public AsyncCommand Download { get; }

    public RelayCommand CancelDownload { get; }

    public AsyncCommand Browse { get; }

    public AsyncCommand Recheck { get; }

    public RelayCommand ShowDetails { get; }

    private void Apply(RcloneStatus s)
    {
        _status = s;
        OnPropertiesChanged(nameof(Status), nameof(State), nameof(Headline), nameof(Message), nameof(Location), nameof(NeedsAction), nameof(IsReady));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RecheckAsync()
    {
        IsBusy = true;
        try
        {
            Progress = "Checking rclone…";
            ProgressIndeterminate = true;
            await _host.RecheckRcloneAsync();
        }
        finally
        {
            Progress = null;
            ProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private async Task DownloadAsync()
    {
        var target = RcloneLocator.ManagedDirectory;
        if (!UiErrors.Confirm(
                "CloudSBiian will download the official rclone release for Windows from downloads.rclone.org " +
                "(about 25 MB), check it against the release's SHA-256 checksums and store it in:\n\n" + target +
                "\n\nrclone is open-source software under the MIT license. No account data is sent. Continue?",
                "Download rclone"))
        {
            return;
        }

        IsBusy = true;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_host.ShutdownToken);
        OnPropertyChanged(nameof(IsDownloading));
        var progress = new global::System.Progress<RcloneDownloadProgress>(p =>
        {
            ProgressIndeterminate = p.BytesTotal is null or 0;
            ProgressPercent = p.BytesTotal is > 0 ? 100.0 * p.BytesDone / p.BytesTotal.Value : 0;
            Progress = p.BytesTotal is > 0
                ? $"{p.Stage}: {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal.Value)}"
                : p.Stage + "…";
        });
        try
        {
            var status = await _host.InstallRcloneAsync(progress, _downloadCts.Token);
            if (!status.IsReady)
            {
                UiErrors.Show(new InvalidOperationException("rclone was downloaded but the check failed: " + status.Message));
            }
        }
        catch (OperationCanceledException)
        {
            Progress = "Download cancelled.";
            await Task.Delay(1500);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            OnPropertyChanged(nameof(IsDownloading));
            Progress = null;
            ProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private async Task BrowseAsync()
    {
        var dlg = new OpenFileDialog { Filter = "rclone.exe|rclone.exe|Executables (*.exe)|*.exe", Title = "Select rclone.exe" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Progress = "Checking the selected file…";
            ProgressIndeterminate = true;
            var (_, line, error) = await RcloneLocator.ValidateAsync(dlg.FileName, _host.ShutdownToken);
            if (error != null)
            {
                UiErrors.Show(new InvalidOperationException(error + " The setting was not changed."));
                return;
            }

            _host.Log.Info("rclone", $"User selected {dlg.FileName} ({line}).");
            await _host.UseRcloneExecutableAsync(dlg.FileName);
        }
        finally
        {
            Progress = null;
            ProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    public string BuildReport()
    {
        var s = _status;
        var sb = new StringBuilder();
        sb.AppendLine($"State:        {s.State}");
        sb.AppendLine($"Message:      {SecretRedactor.Redact(s.Message)}");
        sb.AppendLine($"Executable:   {s.ExecutablePath ?? "—"}");
        sb.AppendLine($"Found via:    {s.Source ?? "—"}");
        sb.AppendLine($"Version:      {s.Version ?? "—"} (minimum v{RcloneLocator.MinimumVersion.ToString(3)})");
        sb.AppendLine($"Backends:     {(s.IsReady ? s.BackendCount.ToString() : "—")}");
        sb.AppendLine($"Checked at:   {(s.CheckedAt == DateTimeOffset.MinValue ? "—" : Format.Time(s.CheckedAt))}");
        sb.AppendLine($"Settings path:{(string.IsNullOrWhiteSpace(_host.Settings.Current.RclonePath) ? " (automatic)" : " " + _host.Settings.Current.RclonePath)}");
        sb.AppendLine($"Download dir: {ManagedDirectory}");
        var catalog = _host.Accounts.Catalog;
        if (s.IsReady && catalog != null)
        {
            sb.AppendLine();
            sb.AppendLine("Backends used by CloudSBiian:");
            foreach (var b in new[] { "drive", "onedrive", "dropbox", "sftp", "s3", "smb", "webdav", "terabox" })
            {
                sb.AppendLine($"  {b,-10} {(catalog.Has(b) ? "available" : "NOT available in this rclone build")}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Locations checked ({s.Searched.Count}), in order:");
        foreach (var c in s.Searched)
        {
            sb.AppendLine($"  [{(c.Exists ? "found" : "  -  ")}] {c.Source,-26} {c.Path}");
        }

        return sb.ToString();
    }
}
