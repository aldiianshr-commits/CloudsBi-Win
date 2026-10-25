using System.Collections.ObjectModel;
using CloudSBiian.CacheEngine;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record ActiveTransferRow(string Account, string Path, string Direction, double Progress, string Bytes, string Speed, string Elapsed);

public sealed record QueueRow(long Id, string AccountId, string Account, string Path, string Size, string State, int Attempts, string Error, string NextAttempt);

public sealed record HistoryRow(string Time, string Account, string Path, string Direction, string Outcome, string Bytes, string Speed, string Error);

public sealed class TransfersViewModel : PageViewModel
{
    private readonly AppHost _host;
    private string? _filterAccountId;
    private Choice? _filter;
    private QueueRow? _selectedQueue;
    private int _tick;

    public TransfersViewModel(AppHost host)
        : base("Transfer Queue", "")
    {
        _host = host;
        Retry = new RelayCommand(() =>
        {
            _host.Uploads.Retry(SelectedQueue!.Id);
            LoadQueue();
        }, () => SelectedQueue is { State: not "Conflict" });
        RetryAll = new RelayCommand(() =>
        {
            _host.Uploads.RetryAll(FilterAccountId);
            LoadQueue();
        });
        Discard = new RelayCommand(() =>
        {
            var q = SelectedQueue!;
            if (UiErrors.Confirm($"Discard the local changes to {q.Path}? They were not uploaded and cannot be recovered."))
            {
                _host.Uploads.Cancel(q.AccountId, q.Path);
                _host.Cache.Metadata.Invalidate(q.AccountId, Core.Paths.CloudPath.GetParent(q.Path));
                LoadQueue();
            }
        }, () => SelectedQueue != null);
        KeepLocal = new AsyncCommand(() => ResolveAsync(ConflictResolution.KeepLocal, "Overwrite the cloud version with your local changes?"), IsConflict);
        KeepBoth = new AsyncCommand(() => ResolveAsync(ConflictResolution.KeepBoth, null), IsConflict);
        DiscardLocal = new AsyncCommand(() => ResolveAsync(ConflictResolution.DiscardLocal, "Throw away your local changes and keep the cloud version?"), IsConflict);
    }

    public ObservableCollection<Choice> Filters { get; } = new();

    public ObservableCollection<ActiveTransferRow> Active { get; } = new();

    public ObservableCollection<QueueRow> Queue { get; } = new();

    public ObservableCollection<HistoryRow> History { get; } = new();

    public string? FilterAccountId
    {
        get => _filterAccountId;
        set
        {
            _filterAccountId = value;
            _filter = Filters.FirstOrDefault(f => f.Id == value);
            OnPropertyChanged(nameof(Filter));
        }
    }

    public Choice? Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                _filterAccountId = value?.Id;
                Refresh();
                LoadQueue();
            }
        }
    }

    public QueueRow? SelectedQueue { get => _selectedQueue; set => SetProperty(ref _selectedQueue, value); }

    public RelayCommand Retry { get; }

    public RelayCommand RetryAll { get; }

    public RelayCommand Discard { get; }

    public AsyncCommand KeepLocal { get; }

    public AsyncCommand KeepBoth { get; }

    public AsyncCommand DiscardLocal { get; }

    private bool IsConflict() => SelectedQueue?.State == nameof(UploadState.Conflict);

    private string AccountName(string id) => _host.Accounts.Get(id)?.Account.DisplayName ?? id;

    public override void OnActivated()
    {
        var keep = _filterAccountId;
        Filters.Clear();
        Filters.Add(new Choice(null, "All accounts"));
        foreach (var r in _host.Accounts.Runtimes)
        {
            Filters.Add(new Choice(r.Id, r.Account.DisplayName));
        }

        _filter = Filters.FirstOrDefault(f => f.Id == keep) ?? Filters[0];
        _filterAccountId = _filter.Id;
        OnPropertyChanged(nameof(Filter));
        LoadQueue();
        base.OnActivated();
    }

    public override void Refresh()
    {
        Active.Clear();
        foreach (var t in _host.Transfers.Active.Where(t => _filterAccountId == null || t.AccountId == _filterAccountId))
        {
            Active.Add(new ActiveTransferRow(
                AccountName(t.AccountId),
                t.Path,
                t.Direction.ToString(),
                (t.Progress ?? 0) * 100,
                t.TotalBytes is long total ? $"{Format.Bytes(t.BytesTransferred)} / {Format.Bytes(total)}" : Format.Bytes(t.BytesTransferred),
                Format.Speed(t.BytesPerSecond),
                Format.Duration(t.Elapsed.TotalSeconds)));
        }

        if (_tick++ % 3 == 0)
        {
            LoadQueue();
            History.Clear();
            foreach (var t in _host.Transfers.Recent.Where(t => _filterAccountId == null || t.AccountId == _filterAccountId).Take(100))
            {
                History.Add(new HistoryRow(
                    t.FinishedAt?.ToString("HH:mm:ss") ?? string.Empty,
                    AccountName(t.AccountId),
                    t.Path,
                    t.Direction.ToString(),
                    t.Outcome.ToString(),
                    Format.Bytes(t.BytesTransferred),
                    Format.Speed(t.BytesPerSecond),
                    t.Error ?? string.Empty));
            }
        }
    }

    private void LoadQueue()
    {
        var selected = SelectedQueue?.Id;
        Queue.Clear();
        foreach (var i in _host.Uploads.GetAll(_filterAccountId))
        {
            Queue.Add(new QueueRow(
                i.Id,
                i.AccountId,
                AccountName(i.AccountId),
                i.Path,
                Format.Bytes(i.Size),
                i.State.ToString(),
                i.Attempts,
                i.LastError ?? string.Empty,
                i.NextAttemptAt > DateTimeOffset.UtcNow ? i.NextAttemptAt.ToLocalTime().ToString("HH:mm:ss") : string.Empty));
        }

        foreach (var s in _host.Uploads.RecentlySynced.Where(s => _filterAccountId == null || s.AccountId == _filterAccountId).Take(20))
        {
            Queue.Add(new QueueRow(-s.Id, s.AccountId, AccountName(s.AccountId), s.Path, Format.Bytes(s.Size), "Synced", s.Attempts, string.Empty, s.UpdatedAt.ToLocalTime().ToString("HH:mm:ss")));
        }

        SelectedQueue = Queue.FirstOrDefault(q => q.Id == selected);
    }

    private async Task ResolveAsync(ConflictResolution resolution, string? confirm)
    {
        var q = SelectedQueue;
        if (q == null || (confirm != null && !UiErrors.Confirm(confirm)))
        {
            return;
        }

        await _host.Uploads.ResolveConflictAsync(q.Id, resolution, _host.ShutdownToken);
        Status = $"Conflict on {q.Path} resolved: {resolution}.";
        LoadQueue();
    }
}
