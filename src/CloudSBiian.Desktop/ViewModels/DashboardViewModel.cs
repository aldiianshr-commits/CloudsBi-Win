using System.Collections.ObjectModel;
using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Logging;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record AccountStatusRow(string Name, string Provider, string State, string Detail);

public sealed record ActivityRow(string Time, string Level, string Category, string Message);

public sealed class DashboardViewModel : PageViewModel
{
    private readonly AppHost _host;
    private string _driveStatus = "—";
    private string _driveDetail = string.Empty;
    private string _accountsSummary = "—";
    private string _transferSummary = "—";
    private string _transferDetail = string.Empty;
    private string _cacheSummary = "—";
    private string _cacheDetail = string.Empty;
    private double _cacheUsage;
    private string _hitRate = "N/A";
    private string _hitDetail = string.Empty;
    private string _pendingUploads = "0";
    private string _pendingDetail = string.Empty;
    private string _prefetchSummary = "N/A";
    private string _prefetchDetail = string.Empty;
    private string _latency = "N/A";
    private string _latencyDetail = string.Empty;
    private IReadOnlyList<double> _downSeries = Array.Empty<double>();
    private IReadOnlyList<double> _upSeries = Array.Empty<double>();
    private string _chartLegend = string.Empty;

    public DashboardViewModel(AppHost host)
        : base("Dashboard", "")
    {
        _host = host;
        GoAccounts = new RelayCommand(() => MainWindow.Current?.Navigate<AccountsViewModel>());
        GoDrive = new RelayCommand(() => MainWindow.Current?.Navigate<DriveViewModel>());
        GoTransfers = new RelayCommand(() => MainWindow.Current?.Navigate<TransfersViewModel>());
    }

    public RelayCommand GoAccounts { get; }

    public RelayCommand GoDrive { get; }

    public RelayCommand GoTransfers { get; }

    public ObservableCollection<AccountStatusRow> TeraBoxAccounts { get; } = new();

    public ObservableCollection<ActivityRow> RecentProblems { get; } = new();

    public string DriveStatus { get => _driveStatus; private set => SetProperty(ref _driveStatus, value); }

    public string DriveDetail { get => _driveDetail; private set => SetProperty(ref _driveDetail, value); }

    public string AccountsSummary { get => _accountsSummary; private set => SetProperty(ref _accountsSummary, value); }

    public string TransferSummary { get => _transferSummary; private set => SetProperty(ref _transferSummary, value); }

    public string TransferDetail { get => _transferDetail; private set => SetProperty(ref _transferDetail, value); }

    public string CacheSummary { get => _cacheSummary; private set => SetProperty(ref _cacheSummary, value); }

    public string CacheDetail { get => _cacheDetail; private set => SetProperty(ref _cacheDetail, value); }

    public double CacheUsage { get => _cacheUsage; private set => SetProperty(ref _cacheUsage, value); }

    public string HitRate { get => _hitRate; private set => SetProperty(ref _hitRate, value); }

    public string HitDetail { get => _hitDetail; private set => SetProperty(ref _hitDetail, value); }

    public string PendingUploads { get => _pendingUploads; private set => SetProperty(ref _pendingUploads, value); }

    public string PendingDetail { get => _pendingDetail; private set => SetProperty(ref _pendingDetail, value); }

    public string PrefetchSummary { get => _prefetchSummary; private set => SetProperty(ref _prefetchSummary, value); }

    public string PrefetchDetail { get => _prefetchDetail; private set => SetProperty(ref _prefetchDetail, value); }

    public string Latency { get => _latency; private set => SetProperty(ref _latency, value); }

    public string LatencyDetail { get => _latencyDetail; private set => SetProperty(ref _latencyDetail, value); }

    public IReadOnlyList<double> DownSeries { get => _downSeries; private set => SetProperty(ref _downSeries, value); }

    public IReadOnlyList<double> UpSeries { get => _upSeries; private set => SetProperty(ref _upSeries, value); }

    public string ChartLegend { get => _chartLegend; private set => SetProperty(ref _chartLegend, value); }

    private int _slowTick;

    public override void Refresh()
    {
        var drives = _host.Drives.Mounted;
        if (drives.Count == 0)
        {
            var winfsp = DriveManager.CheckWinFsp();
            DriveStatus = "Not mounted";
            DriveDetail = winfsp.Installed ? "WinFsp ready. Mount a drive on the Virtual Drive page." : winfsp.Message;
        }
        else
        {
            DriveStatus = string.Join("  ", drives.Select(d => d.Letter + ":"));
            DriveDetail = $"{drives.Sum(d => d.Core.OpenHandles)} open handle(s) · mounted since {drives.Min(d => d.MountedAt):HH:mm}";
        }

        var runtimes = _host.Accounts.Runtimes;
        var connected = runtimes.Count(r => r.State == AccountConnectionState.Connected);
        AccountsSummary = $"{connected} / {runtimes.Count}";

        var tera = runtimes.Where(r => r.Account.Kind == ProviderKind.TeraBox).ToList();
        if (TeraBoxAccounts.Count != tera.Count || tera.Where((r, i) => TeraBoxAccounts[i].Name != r.Account.DisplayName || TeraBoxAccounts[i].State != r.State.ToString()).Any())
        {
            TeraBoxAccounts.Clear();
            foreach (var r in tera)
            {
                TeraBoxAccounts.Add(new AccountStatusRow(r.Account.DisplayName, "TeraBox", r.State.ToString(), r.LastError ?? Format.Bytes(r.Quota?.Used) + " used"));
            }
        }

        var active = _host.Transfers.Active;
        var down = _host.Transfers.CurrentSpeed(TransferDirection.Download, TransferDirection.Prefetch, TransferDirection.LocalInstall);
        var up = _host.Transfers.CurrentSpeed(TransferDirection.Upload);
        TransferSummary = $"↓ {Format.Speed(down)}   ↑ {Format.Speed(up)}";
        TransferDetail = $"{active.Count(t => t.Direction != TransferDirection.Upload)} download(s), {active.Count(t => t.Direction == TransferDirection.Upload)} upload(s) active · total ↓ {Format.Bytes(_host.Metrics.Get(MetricNames.BytesDownloaded))} ↑ {Format.Bytes(_host.Metrics.Get(MetricNames.BytesUploaded))}";

        var settings = _host.Settings.Current;
        var physical = _host.Cache.Store.TotalPhysicalBytes;
        CacheSummary = $"{Format.Bytes(physical)} / {Format.Bytes(settings.CacheBudgetBytes)}";
        CacheUsage = settings.CacheBudgetBytes == 0 ? 0 : Math.Min(100, physical * 100.0 / settings.CacheBudgetBytes);
        var free = _host.Cache.DiskFreeBytes();
        CacheDetail = $"Physical disk usage · disk free {Format.Bytes(free)} · floor {Format.Bytes(settings.MinFreeDiskBytes)}";

        HitRate = Format.Percent(_host.Metrics.CacheHitRate());
        HitDetail = $"{_host.Metrics.Get(MetricNames.CacheHits)} hits · {_host.Metrics.Get(MetricNames.CacheMisses)} misses · {_host.Metrics.Get(MetricNames.CacheEvictions)} evictions";

        var pred = _host.Prediction.GetStats();
        PrefetchSummary = pred.Enabled ? Format.Percent(_host.Metrics.PrefetchAccuracy()) : "Off";
        PrefetchDetail = $"{_host.Metrics.Get(MetricNames.PrefetchIssued)} blocks prefetched ({Format.Bytes(_host.Metrics.Get(MetricNames.PrefetchBytes))}) · {_host.Metrics.Get(MetricNames.PrefetchUseful)} used · {_host.Metrics.Get(MetricNames.PrefetchCancelled)} preempted";

        var read = _host.Metrics.GetLatency(MetricNames.ReadLatency);
        var provider = _host.Metrics.GetLatency(MetricNames.ProviderLatency);
        Latency = Format.Ms(read?.AverageMs);
        LatencyDetail = read == null
            ? "No reads measured yet."
            : $"Reads p95 {Format.Ms(read.P95Ms)} · backend fetch avg {Format.Ms(provider?.AverageMs)} ({read.Samples} samples)";

        var samples = _host.Sampler.Samples;
        DownSeries = samples.Select(s => (double)s.DownloadBytes).ToList();
        UpSeries = samples.Select(s => (double)s.UploadBytes).ToList();
        ChartLegend = samples.Count == 0 ? "Collecting samples…" : $"Last {samples.Count} s · peak ↓ {Format.Speed(samples.Max(s => s.DownloadBytes))} · peak ↑ {Format.Speed(samples.Max(s => s.UploadBytes))}";

        if (_slowTick++ % 5 == 0)
        {
            var pendingCount = _host.Uploads.CountPending();
            PendingUploads = pendingCount.ToString();
            PendingDetail = $"{_host.Uploads.CountByState(UploadState.RetryRequired)} need retry · {_host.Uploads.CountByState(UploadState.Conflict)} conflicts · {_host.Uploads.CountByState(UploadState.Offline)} offline · {_host.Metrics.Get(MetricNames.FailedOperations)} failed transfers";
            RecentProblems.Clear();
            foreach (var e in _host.ActivityLog.Query(LogLevel.Warning, null, 6))
            {
                RecentProblems.Add(new ActivityRow(e.Timestamp.ToString("HH:mm:ss"), e.Level.ToString(), e.Category, e.Message));
            }
        }
    }
}
