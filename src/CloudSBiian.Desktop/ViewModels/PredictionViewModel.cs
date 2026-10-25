using System.Collections.ObjectModel;
using System.Globalization;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record TopFileRow(string Account, string Path, long Opens, string ReadBytes, string Pattern, string LastOpen);

public sealed class PredictionViewModel : PageViewModel
{
    private readonly AppHost _host;
    private bool _enabled;
    private PredictionAggressiveness _aggressiveness;
    private string _bandwidthMBps = "8";
    private string _stats = string.Empty;
    private string _metrics = string.Empty;
    private int _tick;

    public PredictionViewModel(AppHost host)
        : base("Prediction Engine", "")
    {
        _host = host;
        Save = new RelayCommand(SaveSettings);
        Clear = new RelayCommand(() =>
        {
            if (UiErrors.Confirm("Delete all access profiles and recorded launch traces? Prediction starts learning again from scratch."))
            {
                _host.Profiles.Clear();
                _host.Prediction.ClearQueue();
                Status = "Profiles deleted.";
                LoadTop();
            }
        });
        Load();
    }

    public PredictionAggressiveness[] Levels { get; } = Enum.GetValues<PredictionAggressiveness>();

    public ObservableCollection<TopFileRow> TopFiles { get; } = new();

    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

    public PredictionAggressiveness Aggressiveness { get => _aggressiveness; set => SetProperty(ref _aggressiveness, value); }

    public string BandwidthMBps { get => _bandwidthMBps; set => SetProperty(ref _bandwidthMBps, value); }

    public string Stats { get => _stats; private set => SetProperty(ref _stats, value); }

    public string Metrics { get => _metrics; private set => SetProperty(ref _metrics, value); }

    public RelayCommand Save { get; }

    public RelayCommand Clear { get; }

    private void Load()
    {
        var s = _host.Settings.Current;
        Enabled = s.PredictionEnabled;
        Aggressiveness = s.PredictionAggressiveness;
        BandwidthMBps = (s.PrefetchBandwidthLimitBytesPerSecond / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture);
    }

    public override void OnActivated()
    {
        Load();
        LoadTop();
        base.OnActivated();
    }

    public override void Refresh()
    {
        var st = _host.Prediction.GetStats();
        Stats = $"{(st.Enabled ? "Active" : "Disabled")} · level {st.Aggressiveness} · queue {st.QueueLength} · {st.RequestsIssued} requests issued, {st.RequestsDropped} dropped (duplicates/budget) · " +
                $"{Format.Bytes(st.BytesPrefetchedThisHour)} prefetched this hour of {Format.Bytes(st.HourlyBudgetBytes)} hourly budget · " +
                $"profiles: {st.ProfiledFiles} files, {st.CoAccessPairs} co-access pairs, {st.TraceChunks} trace blocks";
        var m = _host.Metrics;
        Metrics = $"Blocks prefetched: {m.Get(MetricNames.PrefetchIssued)} ({Format.Bytes(m.Get(MetricNames.PrefetchBytes))}) · later used by a real read: {m.Get(MetricNames.PrefetchUseful)} · " +
                  $"accuracy {Format.Percent(m.PrefetchAccuracy())} · preempted by active reads: {m.Get(MetricNames.PrefetchCancelled)} · overall cache hit rate {Format.Percent(m.CacheHitRate())}";
        if (_tick++ % 10 == 0)
        {
            LoadTop();
        }
    }

    private void LoadTop()
    {
        TopFiles.Clear();
        foreach (var f in _host.Profiles.TopFiles(30))
        {
            var account = _host.Accounts.Get(f.AccountId)?.Account.DisplayName ?? f.AccountId;
            TopFiles.Add(new TopFileRow(account, f.Path, f.OpenCount, Format.Bytes(f.ReadBytes),
                f.SequentialReads + f.RandomReads == 0 ? "—" : $"{f.SequentialRatio * 100:0}% sequential", Format.Time(f.LastOpen)));
        }
    }

    private void SaveSettings()
    {
        if (!double.TryParse(BandwidthMBps.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps < 0)
        {
            throw new FormatException("Bandwidth limit must be a number (MB/s). Use 0 for unlimited.");
        }

        var previous = _host.Settings.Current.Clone();
        var s = _host.Settings.Current.Clone();
        s.PredictionEnabled = Enabled;
        s.PredictionAggressiveness = Aggressiveness;
        s.PrefetchBandwidthLimitBytesPerSecond = (long)(mbps * 1024 * 1024);
        _ = _host.ApplySettingsAsync(s, previous);
        Status = "Prediction settings saved.";
    }
}
