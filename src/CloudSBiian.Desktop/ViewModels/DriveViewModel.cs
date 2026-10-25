using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record Choice(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record MountedRow(string Letter, string Accounts, string Mode, string Handles, string Since);

public sealed class DriveViewModel : PageViewModel
{
    private readonly AppHost _host;
    private string _winFspStatus = string.Empty;
    private bool _winFspInstalled;
    private string? _selectedLetter;
    private Choice? _selectedAccount;
    private string _volumeLabel = "CloudSBiian";
    private bool _networkDrive;
    private bool _readOnly;
    private bool _mountOnStartup = true;
    private MountedRow? _selectedMounted;

    public DriveViewModel(AppHost host)
        : base("Virtual Drive", "")
    {
        _host = host;
        Mount = new RelayCommand(DoMount, () => _winFspInstalled && SelectedLetter != null);
        Unmount = new RelayCommand(DoUnmount, () => SelectedMounted != null);
        OpenExplorer = new RelayCommand(() =>
        {
            if (SelectedMounted != null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", SelectedMounted.Letter + ":\\") { UseShellExecute = true });
            }
        }, () => SelectedMounted != null);
        InstallWinFsp = new RelayCommand(DoInstallWinFsp);
        RefreshLetters = new RelayCommand(LoadChoices);
        var first = host.Settings.Current.Mounts.FirstOrDefault();
        if (first != null)
        {
            _volumeLabel = first.VolumeLabel;
            _networkDrive = first.NetworkDrive;
            _readOnly = first.ReadOnly;
            _mountOnStartup = first.MountOnStartup;
        }

        LoadChoices();
    }

    public ObservableCollection<string> Letters { get; } = new();

    public ObservableCollection<Choice> AccountChoices { get; } = new();

    public ObservableCollection<MountedRow> MountedDrives { get; } = new();

    public string WinFspStatus { get => _winFspStatus; private set => SetProperty(ref _winFspStatus, value); }

    public bool WinFspInstalled { get => _winFspInstalled; private set => SetProperty(ref _winFspInstalled, value); }

    public string? SelectedLetter { get => _selectedLetter; set => SetProperty(ref _selectedLetter, value); }

    public Choice? SelectedAccount { get => _selectedAccount; set => SetProperty(ref _selectedAccount, value); }

    public string VolumeLabel { get => _volumeLabel; set => SetProperty(ref _volumeLabel, value); }

    public bool NetworkDrive { get => _networkDrive; set => SetProperty(ref _networkDrive, value); }

    public bool ReadOnly { get => _readOnly; set => SetProperty(ref _readOnly, value); }

    public bool MountOnStartup { get => _mountOnStartup; set => SetProperty(ref _mountOnStartup, value); }

    public MountedRow? SelectedMounted { get => _selectedMounted; set => SetProperty(ref _selectedMounted, value); }

    public RelayCommand Mount { get; }

    public RelayCommand Unmount { get; }

    public RelayCommand OpenExplorer { get; }

    public RelayCommand InstallWinFsp { get; }

    public RelayCommand RefreshLetters { get; }

    public override void OnActivated()
    {
        LoadChoices();
        base.OnActivated();
    }

    private void LoadChoices()
    {
        var status = DriveManager.CheckWinFsp();
        WinFspInstalled = status.Installed;
        WinFspStatus = status.Message;

        var previousLetter = SelectedLetter ?? _host.Settings.Current.Mounts.FirstOrDefault()?.DriveLetter;
        Letters.Clear();
        foreach (var l in DriveManager.AvailableLetters())
        {
            Letters.Add(l);
        }

        SelectedLetter = previousLetter != null && Letters.Contains(previousLetter) ? previousLetter : Letters.Contains("X") ? "X" : Letters.FirstOrDefault();

        var previousAccount = SelectedAccount?.Id;
        AccountChoices.Clear();
        AccountChoices.Add(new Choice(null, "All accounts (one folder per account)"));
        foreach (var r in _host.Accounts.Runtimes)
        {
            AccountChoices.Add(new Choice(r.Id, $"{r.Account.DisplayName} only"));
        }

        SelectedAccount = AccountChoices.FirstOrDefault(c => c.Id == previousAccount) ?? AccountChoices[0];
    }

    public override void Refresh()
    {
        var mounted = _host.Drives.Mounted;
        if (MountedDrives.Count != mounted.Count || MountedDrives.Zip(mounted).Any(p => p.First.Letter != p.Second.Letter || p.First.Handles != p.Second.Core.OpenHandles.ToString()))
        {
            var selected = SelectedMounted?.Letter;
            MountedDrives.Clear();
            foreach (var d in mounted)
            {
                var accounts = d.Core.SingleAccount
                    ? _host.Accounts.Get(d.Settings.AccountIds[0])?.Account.DisplayName ?? "?"
                    : string.Join(", ", d.Core.AccountFolders.Select(f => f.Folder));
                var mode = (d.Settings.ReadOnly ? "read-only" : "read/write") + (d.Settings.NetworkDrive ? ", network drive" : string.Empty);
                MountedDrives.Add(new MountedRow(d.Letter, string.IsNullOrEmpty(accounts) ? "(no accounts)" : accounts, mode, d.Core.OpenHandles.ToString(), d.MountedAt.ToString("yyyy-MM-dd HH:mm")));
            }

            SelectedMounted = MountedDrives.FirstOrDefault(m => m.Letter == selected) ?? MountedDrives.FirstOrDefault();
        }
    }

    private void DoMount()
    {
        if (SelectedLetter == null)
        {
            return;
        }

        var settings = new DriveMountSettings
        {
            DriveLetter = SelectedLetter,
            AccountIds = SelectedAccount?.Id == null ? new List<string>() : new List<string> { SelectedAccount.Id },
            VolumeLabel = string.IsNullOrWhiteSpace(VolumeLabel) ? "CloudSBiian" : VolumeLabel.Trim(),
            NetworkDrive = NetworkDrive,
            ReadOnly = ReadOnly,
            MountOnStartup = MountOnStartup,
        };
        _host.Drives.Mount(settings);

        var app = _host.Settings.Current.Clone();
        app.Mounts.RemoveAll(m => string.Equals(m.DriveLetter, settings.DriveLetter, StringComparison.OrdinalIgnoreCase));
        app.Mounts.Add(settings);
        _host.Settings.Save(app);
        Status = $"Mounted {settings.DriveLetter}:. Open it in File Explorer.";
        LoadChoices();
        Refresh();
    }

    private void DoUnmount()
    {
        if (SelectedMounted == null)
        {
            return;
        }

        var handles = _host.Drives.Get(SelectedMounted.Letter)?.Core.OpenHandles ?? 0;
        if (handles > 0 && !UiErrors.Confirm($"{handles} file handle(s) are still open on {SelectedMounted.Letter}:. Programs using these files may lose access. Unmount anyway?"))
        {
            return;
        }

        var letter = SelectedMounted.Letter;
        _host.Drives.Unmount(letter);
        Status = $"Unmounted {letter}:. Pending uploads continue in the background.";
        LoadChoices();
        Refresh();
    }

    private void DoInstallWinFsp()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "dependencies", "winfsp.msi");
        if (File.Exists(bundled))
        {
            if (UiErrors.Confirm("Install the bundled WinFsp driver now? Windows will ask for administrator permission."))
            {
                Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{bundled}\" INSTALLLEVEL=1000") { UseShellExecute = true });
            }
        }
        else
        {
            Process.Start(new ProcessStartInfo("https://winfsp.dev/rel/") { UseShellExecute = true });
        }
    }
}
