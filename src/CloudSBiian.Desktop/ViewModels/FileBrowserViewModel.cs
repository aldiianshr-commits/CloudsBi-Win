using System.Collections.ObjectModel;
using System.IO;
using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record FileRow(string Name, string Path, bool IsDirectory, long Size, string SizeText, string Modified, string CacheState, CloudEntry? Entry)
{
    public string Icon => IsDirectory ? "" : "";
}

public sealed class FileBrowserViewModel : PageViewModel
{
    private readonly AppHost _host;
    private Choice? _account;
    private string _path = CloudPath.Root;
    private FileRow? _selected;
    private bool _stale;

    public FileBrowserViewModel(AppHost host)
        : base("File Browser", "")
    {
        _host = host;
        Up = new AsyncCommand(() => NavigateAsync(CloudPath.GetParent(CurrentPath)), () => CurrentPath != CloudPath.Root);
        Reload = new AsyncCommand(() => LoadAsync(force: true), () => Account != null);
        Open = new AsyncCommand(OpenSelectedAsync, () => Selected?.IsDirectory == true);
        Download = new AsyncCommand(DownloadAsync, () => Selected is { IsDirectory: false });
        Upload = new AsyncCommand(UploadAsync, () => Account != null);
        NewFolder = new AsyncCommand(NewFolderAsync, () => Account != null);
        Rename = new AsyncCommand(RenameAsync, () => Selected != null);
        Delete = new AsyncCommand(DeleteAsync, () => Selected != null);
        Pin = new AsyncCommand(() => TogglePinAsync(true), () => Selected != null);
        Unpin = new AsyncCommand(() => TogglePinAsync(false), () => Selected != null);
        Evict = new AsyncCommand(EvictAsync, () => Selected != null);
    }

    public ObservableCollection<Choice> Accounts { get; } = new();

    public ObservableCollection<FileRow> Items { get; } = new();

    public Choice? Account
    {
        get => _account;
        set
        {
            if (SetProperty(ref _account, value))
            {
                _path = CloudPath.Root;
                OnPropertyChanged(nameof(CurrentPath));
                _ = LoadAsync(false);
            }
        }
    }

    public string CurrentPath => _path;

    public bool IsStale { get => _stale; private set => SetProperty(ref _stale, value); }

    public FileRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public AsyncCommand Up { get; }

    public AsyncCommand Reload { get; }

    public AsyncCommand Open { get; }

    public AsyncCommand Download { get; }

    public AsyncCommand Upload { get; }

    public AsyncCommand NewFolder { get; }

    public AsyncCommand Rename { get; }

    public AsyncCommand Delete { get; }

    public AsyncCommand Pin { get; }

    public AsyncCommand Unpin { get; }

    public AsyncCommand Evict { get; }

    public override void OnActivated()
    {
        var current = Account?.Id;
        Accounts.Clear();
        foreach (var r in _host.Accounts.Runtimes)
        {
            Accounts.Add(new Choice(r.Id, $"{r.Account.DisplayName} ({r.State})"));
        }

        var match = Accounts.FirstOrDefault(a => a.Id == current) ?? Accounts.FirstOrDefault();
        if (!Equals(match, _account))
        {
            Account = match;
        }
    }

    public Task NavigateAsync(string path)
    {
        _path = CloudPath.Normalize(path);
        OnPropertyChanged(nameof(CurrentPath));
        return LoadAsync(false);
    }

    private async Task OpenSelectedAsync()
    {
        if (Selected is { IsDirectory: true } row)
        {
            await NavigateAsync(row.Path);
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (Account?.Id == null)
        {
            Items.Clear();
            return;
        }

        var id = Account.Id;
        Status = "Loading…";
        try
        {
            var provider = _host.Accounts.TryGetProvider(id);
            var listing = await _host.Cache.Metadata.ListAsync(id, provider, CurrentPath, _host.ShutdownToken, force);
            var pending = _host.Uploads.PendingIn(id, CurrentPath);
            var rows = await Task.Run(() => listing.Entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => ToRow(id, e, pending))
                .ToList());
            foreach (var p in pending.Where(p => listing.Entries.All(e => e.Path != p.Path)))
            {
                rows.Add(new FileRow(CloudPath.GetName(p.Path), p.Path, false, p.Size, Format.Bytes(p.Size), Format.Time(p.UpdatedAt), $"Local only · {p.State}", null));
            }

            Items.Clear();
            foreach (var r in rows)
            {
                Items.Add(r);
            }

            IsStale = listing.IsStale;
            Status = listing.IsStale
                ? $"Offline view from {listing.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm} — the account is not reachable."
                : $"{Items.Count} item(s).";
        }
        catch (Exception ex)
        {
            Items.Clear();
            Status = Core.Security.SecretRedactor.Redact(ex.Message);
        }
    }

    private FileRow ToRow(string accountId, CloudEntry e, IReadOnlyList<UploadItem> pending)
    {
        string state;
        if (e.IsDirectory)
        {
            state = _host.Cache.IsPinned(accountId, e.Path) ? "Pinned" : string.Empty;
        }
        else
        {
            var p = pending.FirstOrDefault(x => x.Path == e.Path);
            if (p != null)
            {
                state = $"Modified locally · {p.State}";
            }
            else if (_host.Cache.IsExcluded(accountId, e.Path))
            {
                state = "Excluded from cache";
            }
            else
            {
                var record = _host.Cache.Store.GetFile(accountId, e.Path);
                if (record == null || record.Identity != e.Identity || e.Size == 0)
                {
                    state = "Cloud only";
                }
                else
                {
                    var total = (int)((e.Size - 1) / record.ChunkSize) + 1;
                    var cached = _host.Cache.Store.CachedChunks(accountId, record.FileKey).Count;
                    state = cached == 0 ? "Cloud only" : cached >= total ? "Cached" : $"Partially cached ({cached * 100 / total}%)";
                }

                if (_host.Cache.IsPinned(accountId, e.Path))
                {
                    state += " · Pinned";
                }
            }
        }

        return new FileRow(e.Name, e.Path, e.IsDirectory, e.Size, e.IsDirectory ? string.Empty : Format.Bytes(e.Size), e.IsDirectory ? string.Empty : Format.Time(e.Modified), state, e);
    }

    private ICloudStorageProvider RequireProvider() =>
        _host.Accounts.TryGetProvider(Account!.Id!) ?? throw new InvalidOperationException("The account is not connected.");

    private async Task DownloadAsync()
    {
        if (Selected is not { IsDirectory: false, Entry: not null } row)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = row.Name, Title = "Download to" };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var provider = RequireProvider();
        var accountId = Account!.Id!;
        Status = $"Downloading {row.Name}…";
        using var transfer = _host.Transfers.Begin(accountId, row.Path, TransferDirection.Download, row.Size);
        var tmp = dlg.FileName + ".csb-part";
        try
        {
            await using (var src = await provider.OpenReadAsync(row.Path, 0, null, _host.ShutdownToken))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                var buffer = new byte[1024 * 1024];
                int n;
                while ((n = await src.ReadAsync(buffer, _host.ShutdownToken)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), _host.ShutdownToken);
                    transfer.Add(n);
                }
            }

            if (new FileInfo(tmp).Length != row.Size)
            {
                throw new IOException("The downloaded size does not match the cloud file.");
            }

            File.Move(tmp, dlg.FileName, overwrite: true);
            transfer.Complete();
            Status = $"Downloaded {row.Name} ({Format.Bytes(row.Size)}).";
        }
        catch (Exception ex)
        {
            transfer.Fail(ex.Message);
            File.Delete(tmp);
            throw;
        }
    }

    private async Task UploadAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Upload files", Multiselect = true };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var accountId = Account!.Id!;
        var provider = _host.Accounts.TryGetProvider(accountId);
        var queued = 0;
        foreach (var file in dlg.FileNames)
        {
            var name = Path.GetFileName(file);
            if (!CloudPath.TryValidateName(name, out var error))
            {
                UiErrors.Info($"{name}: {error}");
                continue;
            }

            var target = CloudPath.Combine(CurrentPath, name);
            var existing = await _host.Cache.Metadata.StatAsync(accountId, provider, target, _host.ShutdownToken);
            if (existing is { IsDirectory: true })
            {
                UiErrors.Info($"A folder named {name} already exists here.");
                continue;
            }

            if (existing != null && !UiErrors.Confirm($"{name} already exists in the cloud. Replace it?"))
            {
                continue;
            }

            var staging = _host.Staging.LocalPathFor(accountId, target);
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            await Task.Run(() => File.Copy(file, staging, overwrite: true));
            _host.Uploads.Enqueue(accountId, target, staging, new FileInfo(staging).Length, existing?.Identity);
            queued++;
        }

        Status = $"{queued} file(s) queued for upload. Progress is shown on the Transfer Queue page.";
        await LoadAsync(false);
    }

    private async Task NewFolderAsync()
    {
        var name = InputDialog.Ask("New folder", "Folder name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        CloudPath.ValidateName(name);
        var path = CloudPath.Combine(CurrentPath, name);
        await RequireProvider().CreateDirectoryAsync(path, _host.ShutdownToken);
        _host.Cache.Metadata.Invalidate(Account!.Id!, CurrentPath);
        await LoadAsync(true);
    }

    private async Task RenameAsync()
    {
        if (Selected == null)
        {
            return;
        }

        var name = InputDialog.Ask("Rename", "New name", Selected.Name);
        if (string.IsNullOrWhiteSpace(name) || name == Selected.Name)
        {
            return;
        }

        CloudPath.ValidateName(name);
        var accountId = Account!.Id!;
        var target = CloudPath.Combine(CurrentPath, name);
        if (Selected.Entry == null)
        {
            var newLocal = _host.Staging.Rename(accountId, Selected.Path, target);
            _host.Uploads.Rename(accountId, Selected.Path, target, newLocal);
        }
        else
        {
            await RequireProvider().MoveAsync(Selected.Path, target, Selected.IsDirectory, false, _host.ShutdownToken);
            _host.Cache.Store.RemovePath(accountId, Selected.Path, Selected.IsDirectory);
        }

        _host.Cache.Metadata.Invalidate(accountId, CurrentPath);
        await LoadAsync(true);
    }

    private async Task DeleteAsync()
    {
        if (Selected == null)
        {
            return;
        }

        var row = Selected;
        var accountId = Account!.Id!;
        if (row.Entry == null)
        {
            if (UiErrors.Confirm($"Discard the local file {row.Name}? It was never uploaded, so this cannot be undone."))
            {
                _host.Uploads.Cancel(accountId, row.Path);
                await LoadAsync(false);
            }

            return;
        }

        if (!UiErrors.Confirm($"Delete {(row.IsDirectory ? "the empty folder" : "the file")} \"{row.Name}\" from the cloud? This deletes it on the provider, not only from the cache."))
        {
            return;
        }

        var provider = RequireProvider();
        if (row.IsDirectory)
        {
            await provider.DeleteDirectoryAsync(row.Path, _host.ShutdownToken);
        }
        else
        {
            _host.Uploads.Cancel(accountId, row.Path);
            await provider.DeleteFileAsync(row.Path, _host.ShutdownToken);
        }

        _host.Cache.Store.RemovePath(accountId, row.Path, row.IsDirectory);
        _host.Cache.Metadata.Remove(accountId, row.Path);
        _host.Log.Info("files", $"Deleted {row.Path} from the cloud.", accountId);
        await LoadAsync(true);
    }

    private async Task TogglePinAsync(bool pin)
    {
        if (Selected == null)
        {
            return;
        }

        if (pin)
        {
            _host.Cache.Pin(Account!.Id!, Selected.Path);
            Status = $"Pinned {Selected.Name}: its cached data is never evicted. Use Cache Manager to pre-download pinned items.";
        }
        else
        {
            _host.Cache.Unpin(Account!.Id!, Selected.Path);
            Status = $"Unpinned {Selected.Name}.";
        }

        await LoadAsync(false);
    }

    private async Task EvictAsync()
    {
        if (Selected == null)
        {
            return;
        }

        var removed = _host.Cache.Store.RemovePath(Account!.Id!, Selected.Path, Selected.IsDirectory);
        Status = $"Removed cached data of {removed} file(s). Cloud data is untouched; pending uploads are kept.";
        await LoadAsync(false);
    }
}
