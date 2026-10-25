using System.Collections.ObjectModel;
using System.Windows.Threading;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;
    private PageViewModel? _currentPage;
    private string _footer = string.Empty;

    public MainViewModel(AppHost host)
    {
        _host = host;
        Pages = new ObservableCollection<PageViewModel>
        {
            new DashboardViewModel(host),
            new AccountsViewModel(host),
            new DriveViewModel(host),
            new FileBrowserViewModel(host),
            new CacheViewModel(host),
            new PredictionViewModel(host),
            new LibraryViewModel(host),
            new TransfersViewModel(host),
            new MonitoringViewModel(host),
            new SettingsViewModel(host),
            new LogsViewModel(host),
        };
        CurrentPage = Pages[0];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                value?.OnActivated();
            }
        }
    }

    public string Footer
    {
        get => _footer;
        private set => SetProperty(ref _footer, value);
    }

    public T? Page<T>() where T : PageViewModel => Pages.OfType<T>().FirstOrDefault();

    public void Navigate<T>() where T : PageViewModel
    {
        var page = Page<T>();
        if (page != null)
        {
            CurrentPage = page;
        }
    }

    private void Tick()
    {
        try
        {
            CurrentPage?.Refresh();
            var drives = _host.Drives.Mounted;
            var connected = _host.Accounts.Runtimes.Count(r => r.State == AccountConnectionState.Connected);
            var total = _host.Accounts.Runtimes.Count;
            var down = _host.Transfers.CurrentSpeed(TransferDirection.Download, TransferDirection.Prefetch, TransferDirection.LocalInstall);
            var up = _host.Transfers.CurrentSpeed(TransferDirection.Upload);
            Footer = $"{(drives.Count == 0 ? "No drive mounted" : string.Join(", ", drives.Select(d => d.Letter + ":")))}  ·  {connected}/{total} accounts connected  ·  ↓ {Format.Speed(down)}  ↑ {Format.Speed(up)}";
        }
        catch (Exception ex)
        {
            _host.Log.Debug("ui", $"Refresh failed: {ex.Message}");
        }
    }
}
