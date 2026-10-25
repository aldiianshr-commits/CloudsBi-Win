using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CloudSBiian.Core.Paths;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.GameLauncher;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record LibraryRow(string Id, string Name, string Mode, string Source, string LastStatus, string LastLaunch, bool Running)
{
    public string RunState => Running ? "Running" : string.Empty;
}

public sealed class LibraryViewModel : PageViewModel
{
    private readonly AppHost _host;
    private LibraryRow? _selected;
    private string _editName = string.Empty;
    private string _editArguments = string.Empty;
    private string _editWorkingDir = string.Empty;
    private LaunchMode _editMode;
    private int _editPriority = 1;
    private string _editInfo = string.Empty;

    public LibraryViewModel(AppHost host)
        : base("Game Library", "")
    {
        _host = host;
        AddExecutable = new RelayCommand(AddExe);
        ScanFolder = new RelayCommand(Scan);
        SaveEntry = new RelayCommand(Save, () => Selected != null);
        Prepare = new RelayCommand(() => OpenPrepare(false), () => Selected != null);
        InstallLocal = new RelayCommand(() => OpenPrepare(true), () => Selected != null);
        Launch = new RelayCommand(DoLaunch, () => Selected is { Running: false });
        Stop = new AsyncCommand(StopAsync, () => Selected is { Running: true });
        Remove = new RelayCommand(DoRemove, () => Selected != null);
        ViewLocation = new RelayCommand(OpenLocation, () => Selected != null);
        _host.Library.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Reload);
        _host.Launcher.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(Reload);
    }

    public ObservableCollection<LibraryRow> Entries { get; } = new();

    public LaunchMode[] Modes { get; } = Enum.GetValues<LaunchMode>();

    public int[] Priorities { get; } = [0, 1, 2];

    public LibraryRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                LoadEditor();
            }
        }
    }

    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }

    public string EditArguments { get => _editArguments; set => SetProperty(ref _editArguments, value); }

    public string EditWorkingDir { get => _editWorkingDir; set => SetProperty(ref _editWorkingDir, value); }

    public LaunchMode EditMode { get => _editMode; set => SetProperty(ref _editMode, value); }

    public int EditPriority { get => _editPriority; set => SetProperty(ref _editPriority, value); }

    public string EditInfo { get => _editInfo; private set => SetProperty(ref _editInfo, value); }

    public RelayCommand AddExecutable { get; }

    public RelayCommand ScanFolder { get; }

    public RelayCommand SaveEntry { get; }

    public RelayCommand Prepare { get; }

    public RelayCommand InstallLocal { get; }

    public RelayCommand Launch { get; }

    public AsyncCommand Stop { get; }

    public RelayCommand Remove { get; }

    public RelayCommand ViewLocation { get; }

    public override void OnActivated()
    {
        Reload();
        base.OnActivated();
    }

    private void Reload()
    {
        var selected = Selected?.Id;
        Entries.Clear();
        foreach (var e in _host.Library.GetAll())
        {
            var source = e.IsCloudBased
                ? $"{_host.Accounts.Get(e.AccountId!)?.Account.DisplayName ?? "(removed account)"}: {e.CloudFolder}"
                : e.Executable;
            if (e.Mode == LaunchMode.FullLocal && e.LocalPath != null)
            {
                source += $"  →  {e.LocalPath}";
            }

            Entries.Add(new LibraryRow(e.Id, e.Name, e.Mode.ToString(), source, e.LastStatus ?? "Never launched", Format.Time(e.LastLaunchAt), _host.Launcher.IsRunning(e.Id)));
        }

        _selected = Entries.FirstOrDefault(r => r.Id == selected);
        OnPropertyChanged(nameof(Selected));
    }

    private LibraryEntry? Current => Selected == null ? null : _host.Library.Get(Selected.Id);

    private void LoadEditor()
    {
        var e = Current;
        if (e == null)
        {
            EditInfo = string.Empty;
            return;
        }

        EditName = e.Name;
        EditArguments = e.Arguments;
        EditWorkingDir = e.WorkingDirectory ?? string.Empty;
        EditMode = e.Mode;
        EditPriority = e.CachePriority;
        EditInfo = e.IsCloudBased
            ? $"Executable: {e.Executable} in {e.CloudFolder} · added {Format.Time(e.AddedAt)} · last exit code {e.LastExitCode?.ToString() ?? "—"}"
            : $"Local executable: {e.Executable} · added {Format.Time(e.AddedAt)} · last exit code {e.LastExitCode?.ToString() ?? "—"}";
    }

    private void Save()
    {
        var e = Current;
        if (e == null)
        {
            return;
        }

        var error = LaunchService.ValidateArguments(EditArguments);
        if (error != null)
        {
            throw new InvalidOperationException(error);
        }

        if (EditMode != LaunchMode.FullLocal || e.LocalPath != null || !e.IsCloudBased)
        {
            e.Mode = EditMode;
        }
        else
        {
            UiErrors.Info("Full Local mode needs a local copy first. Use 'Install locally…'.");
        }

        e.Name = string.IsNullOrWhiteSpace(EditName) ? e.Name : EditName.Trim();
        e.Arguments = EditArguments.Trim();
        e.WorkingDirectory = string.IsNullOrWhiteSpace(EditWorkingDir) ? null : EditWorkingDir.Trim();
        e.CachePriority = EditPriority;
        _host.Library.Save(e);
        if (e.IsCloudBased && e.CachePriority >= 2)
        {
            _host.Cache.Pin(e.AccountId!, e.CloudFolder!);
        }

        Status = $"Saved {e.Name}.";
    }

    private void AddExe()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the game or application executable (on a CloudSBiian drive or a local disk)",
            Filter = "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            AddFromPath(dlg.FileName, folder: null);
        }
    }

    private void AddFromPath(string exePath, string? folder)
    {
        var entry = new LibraryEntry
        {
            Id = LibraryEntry.NewId(),
            Name = Path.GetFileNameWithoutExtension(exePath),
            Executable = exePath,
        };
        var gameFolder = folder ?? Path.GetDirectoryName(exePath)!;
        if (_host.Drives.TryMapWindowsPath(exePath, out var accountId, out var cloudExe) &&
            _host.Drives.TryMapWindowsPath(gameFolder, out _, out var cloudFolder))
        {
            entry.AccountId = accountId;
            entry.CloudFolder = cloudFolder;
            entry.Executable = CloudPath.Rebase(cloudExe!, cloudFolder!, CloudPath.Root).TrimStart('/');
            entry.Mode = LaunchMode.HybridOptimized;
            entry.Name = Path.GetFileName(gameFolder.TrimEnd('\\'));
        }
        else
        {
            entry.Mode = LaunchMode.CloudDirect;
        }

        _host.Library.Save(entry);
        Status = entry.IsCloudBased
            ? $"Added {entry.Name} from the cloud drive. Use 'Optimize & Prepare' before the first launch."
            : $"Added {entry.Name} (local application).";
        Reload();
        Selected = Entries.FirstOrDefault(r => r.Id == entry.Id);
    }

    private void Scan()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a game folder (usually on a CloudSBiian drive)" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var root = dlg.FolderName;
        var exes = EnumerateExecutables(root, 3)
            .Where(f => !IsHelperExe(Path.GetFileName(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .Take(200)
            .ToList();
        if (exes.Count == 0)
        {
            UiErrors.Info("No executables found in this folder (searched 3 levels deep).");
            return;
        }

        var picked = PickListDialog.Ask("Scan folder", $"Executables found in {root}. Select the ones to add:", exes);
        foreach (var rel in picked ?? [])
        {
            AddFromPath(Path.Combine(root, rel), root);
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string dir, int depth)
    {
        string[] files;
        string[] dirs;
        try
        {
            files = Directory.GetFiles(dir, "*.exe");
            dirs = depth > 0 ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var f in files)
        {
            yield return f;
        }

        foreach (var d in dirs)
        {
            foreach (var f in EnumerateExecutables(d, depth - 1))
            {
                yield return f;
            }
        }
    }

    private static bool IsHelperExe(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("unins") || n.Contains("crashreport") || n.Contains("vc_redist") || n.StartsWith("dxsetup") || n.Contains("easyanticheat_setup") || n.Contains("dotnetfx");
    }

    private void OpenPrepare(bool install)
    {
        var e = Current;
        if (e == null)
        {
            return;
        }

        if (install && !e.IsCloudBased)
        {
            UiErrors.Info("This entry is already a local application.");
            return;
        }

        new PrepareWindow(_host, e, install).ShowDialog();
        Reload();
    }

    private void DoLaunch()
    {
        var e = Current;
        if (e == null)
        {
            return;
        }

        if (e.IsCloudBased && e.Mode != LaunchMode.FullLocal && _host.Accounts.TryGetProvider(e.AccountId!) == null)
        {
            throw new InvalidOperationException("The account of this entry is not connected. Reconnect it first.");
        }

        var app = _host.Launcher.Launch(e);
        Status = $"Started {e.Name} (PID {app.Process.Id}).";
        Reload();
    }

    private async Task StopAsync()
    {
        if (Selected == null)
        {
            return;
        }

        if (!UiErrors.Confirm($"Stop {Selected.Name}? Unsaved progress in the game/app may be lost."))
        {
            return;
        }

        await _host.Launcher.StopAsync(Selected.Id, TimeSpan.FromSeconds(10));
        Reload();
    }

    private void DoRemove()
    {
        var e = Current;
        if (e == null)
        {
            return;
        }

        if (!UiErrors.Confirm($"Remove {e.Name} from the library? Files in the cloud are not deleted."))
        {
            return;
        }

        if (e.LocalPath != null && Directory.Exists(e.LocalPath) &&
            UiErrors.Confirm($"Also delete the local copy at {e.LocalPath}? Choose No to keep it."))
        {
            Directory.Delete(e.LocalPath, recursive: true);
        }

        _host.Library.Remove(e.Id);
        _host.Profiles.DeleteTrace(e.TraceId);
        Status = $"Removed {e.Name}.";
        Reload();
    }

    private void OpenLocation()
    {
        var e = Current;
        if (e == null)
        {
            return;
        }

        var folder = _host.Launcher.ResolveGameFolder(e, out var error) ?? throw new InvalidOperationException(error);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
