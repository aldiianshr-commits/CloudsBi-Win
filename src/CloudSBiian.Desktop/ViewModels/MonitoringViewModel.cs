using System.Collections.ObjectModel;
using System.Diagnostics;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record CounterRow(string Name, string Value);

public sealed record LatencyRow(string Operation, int Samples, string Average, string P50, string P95, string Max);

public sealed record AccountPerfRow(string Account, string State, string MeasuredSpeed, string Downloads, string CachedOnDisk);

public sealed class MonitoringViewModel : PageViewModel
{
    private readonly AppHost _host;
    private IReadOnlyList<double> _down = Array.Empty<double>();
    private IReadOnlyList<double> _up = Array.Empty<double>();
    private IReadOnlyList<double> _hits = Array.Empty<double>();
    private IReadOnlyList<double> _misses = Array.Empty<double>();
    private string _process = string.Empty;
    private int _tick;

    public MonitoringViewModel(AppHost host)
        : base("Monitoring", "")
    {
        _host = host;
        Reset = new RelayCommand(() =>
        {
            if (UiErrors.Confirm("Reset all in-memory counters and latency samples? (Logs are kept.)"))
            {
                _host.Metrics.Reset();
            }
        });
    }

    public ObservableCollection<CounterRow> Counters { get; } = new();

    public ObservableCollection<LatencyRow> Latencies { get; } = new();

    public ObservableCollection<AccountPerfRow> AccountsPerf { get; } = new();

    public IReadOnlyList<double> DownSeries { get => _down; private set => SetProperty(ref _down, value); }

    public IReadOnlyList<double> UpSeries { get => _up; private set => SetProperty(ref _up, value); }

    public IReadOnlyList<double> HitSeries { get => _hits; private set => SetProperty(ref _hits, value); }

    public IReadOnlyList<double> MissSeries { get => _misses; private set => SetProperty(ref _misses, value); }

    public string ProcessInfo { get => _process; private set => SetProperty(ref _process, value); }

    public RelayCommand Reset { get; }

    public override void Refresh()
    {
        var samples = _host.Sampler.Samples;
        DownSeries = samples.Select(s => (double)s.DownloadBytes).ToList();
        UpSeries = samples.Select(s => (double)s.UploadBytes).ToList();
        HitSeries = samples.Select(s => (double)s.Hits).ToList();
        MissSeries = samples.Select(s => (double)s.Misses).ToList();

        if (_tick++ % 2 != 0)
        {
            return;
        }

        Counters.Clear();
        foreach (var (k, v) in _host.Metrics.Snapshot().OrderBy(k => k.Key))
        {
            var value = k.Contains("bytes") ? Format.Bytes(v) : v.ToString("N0");
            Counters.Add(new CounterRow(k, value));
        }

        if (Counters.Count == 0)
        {
            Counters.Add(new CounterRow("(no operations measured yet)", "N/A"));
        }

        Latencies.Clear();
        foreach (var (k, l) in _host.Metrics.LatencySnapshot().OrderBy(k => k.Key))
        {
            Latencies.Add(new LatencyRow(k, l.Samples, Format.Ms(l.AverageMs), Format.Ms(l.P50Ms), Format.Ms(l.P95Ms), Format.Ms(l.MaxMs)));
        }

        AccountsPerf.Clear();
        var usage = _host.Cache.Store.GetUsage().ToDictionary(u => u.AccountId, u => u.PhysicalBytes);
        foreach (var r in _host.Accounts.Runtimes)
        {
            var (running, waiting) = _host.Cache.QueueDepth(r.Id);
            AccountsPerf.Add(new AccountPerfRow(
                r.Account.DisplayName,
                r.State.ToString(),
                Format.Speed(_host.Transfers.MeasuredSpeed(r.Id)),
                $"{running} running · {waiting} waiting",
                Format.Bytes(usage.TryGetValue(r.Id, out var b) ? b : 0)));
        }

        using var p = Process.GetCurrentProcess();
        var handles = _host.Drives.Mounted.Sum(d => d.Core.OpenHandles);
        ProcessInfo = $"Memory {Format.Bytes(p.WorkingSet64)} · threads {p.Threads.Count} · open drive handles {handles} · .NET {Environment.Version} · rclone {_host.Accounts.RcloneVersion ?? "not started"} · {VirtualDrive.DriveManager.CheckWinFsp().Message}";
    }
}
