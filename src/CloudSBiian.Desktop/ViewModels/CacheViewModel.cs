using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Paths;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;

namespace CloudSBiian.Desktop.ViewModels;

public sealed record CacheUsageRow(string AccountId, string Account, string Physical, string Logical, int Chunks, int Files, string Staging);

public sealed record PinRow(string AccountId, string Account, string Path);

public sealed record ExclusionRow(long Id, string Scope, string Pattern);

public sealed class CacheViewModel : PageViewModel
{
    private readonly AppHost _host;
    private string _budgetChoice = "20";
    private string _customBudgetGb = "60";
    private string _minFreeGb = "10";
    private string _chunkSizeMiB = "4";
    private bool _verifyOnRead = true;
    private bool _writeThrough;
    private string _summary = string.Empty;
    private double _usagePercent;
    private CacheUsageRow? _selectedUsage;
    private PinRow? _selectedPin;
    private ExclusionRow? _selectedExclusion;
    private string _newPattern = string.Empty;
    private Choice? _patternScope;

    public CacheViewModel(AppHost host)
        : base("Cache Manager", "")
    {
        _host = host;
        Save = new RelayCommand(SaveSettings);
        ClearAccount = new RelayCommand(ClearSelected, () => SelectedUsage != null);
        ClearAll = new RelayCommand(ClearEverything);
        Verify = new AsyncCommand(VerifyAsync);
        OpenFolder = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", _host.Cache.CacheDirectory) { UseShellExecute = true }));
        Unpin = new RelayCommand(() =>
        {
            if (SelectedPin != null)
            {
                _host.Cache.Unpin(SelectedPin.AccountId, SelectedPin.Path);
                LoadLists();
            }
        }, () => SelectedPin != null);
        DownloadPinned = new AsyncCommand(DownloadPinnedAsync, () => SelectedPin != null);
        AddExclusion = new RelayCommand(() =>
        {
            _host.Cache.AddExclusion(PatternScope?.Id, NewPattern);
            NewPattern = string.Empty;
            LoadLists();
        }, () => !string.IsNullOrWhiteSpace(NewPattern));
        RemoveExclusion = new RelayCommand(() =>
        {
            if (SelectedExclusion != null)
            {
                _host.Cache.RemoveExclusion(SelectedExclusion.Id);
                LoadLists();
            }
        }, () => SelectedExclusion != null);
        LoadSettings();
    }

    public string[] BudgetOptions { get; } = ["10", "20", "30", "40", "Custom"];

    public ObservableCollection<CacheUsageRow> Usage { get; } = new();

    public ObservableCollection<PinRow> Pins { get; } = new();

    public ObservableCollection<ExclusionRow> Exclusions { get; } = new();

    public ObservableCollection<Choice> Scopes { get; } = new();

    public string BudgetChoice
    {
        get => _budgetChoice;
        set
        {
            if (SetProperty(ref _budgetChoice, value))
            {
                OnPropertyChanged(nameof(IsCustom));
            }
        }
    }

    public bool IsCustom => BudgetChoice == "Custom";

    public string CustomBudgetGb { get => _customBudgetGb; set => SetProperty(ref _customBudgetGb, value); }

    public string MinFreeGb { get => _minFreeGb; set => SetProperty(ref _minFreeGb, value); }

    public string ChunkSizeMiB { get => _chunkSizeMiB; set => SetProperty(ref _chunkSizeMiB, value); }

    public bool VerifyOnRead { get => _verifyOnRead; set => SetProperty(ref _verifyOnRead, value); }

    public bool WriteThrough { get => _writeThrough; set => SetProperty(ref _writeThrough, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public double UsagePercent { get => _usagePercent; private set => SetProperty(ref _usagePercent, value); }

    public string CacheDirectory => _host.Cache.CacheDirectory;

    public CacheUsageRow? SelectedUsage { get => _selectedUsage; set => SetProperty(ref _selectedUsage, value); }

    public PinRow? SelectedPin { get => _selectedPin; set => SetProperty(ref _selectedPin, value); }

    public ExclusionRow? SelectedExclusion { get => _selectedExclusion; set => SetProperty(ref _selectedExclusion, value); }

    public string NewPattern { get => _newPattern; set => SetProperty(ref _newPattern, value); }

    public Choice? PatternScope { get => _patternScope; set => SetProperty(ref _patternScope, value); }

    public RelayCommand Save { get; }

    public RelayCommand ClearAccount { get; }

    public RelayCommand ClearAll { get; }

    public AsyncCommand Verify { get; }

    public RelayCommand OpenFolder { get; }

    public RelayCommand Unpin { get; }

    public AsyncCommand DownloadPinned { get; }

    public RelayCommand AddExclusion { get; }

    public RelayCommand RemoveExclusion { get; }

    private void LoadSettings()
    {
        var s = _host.Settings.Current;
        var gb = s.CacheBudgetBytes / AppSettings.GiB;
        var choice = gb.ToString(CultureInfo.InvariantCulture);
        BudgetChoice = s.CacheBudgetBytes % AppSettings.GiB == 0 && BudgetOptions.Contains(choice) ? choice : "Custom";
        CustomBudgetGb = (s.CacheBudgetBytes / (double)AppSettings.GiB).ToString("0.##", CultureInfo.InvariantCulture);
        MinFreeGb = (s.MinFreeDiskBytes / (double)AppSettings.GiB).ToString("0.##", CultureInfo.InvariantCulture);
        ChunkSizeMiB = s.ChunkSizeMiB.ToString(CultureInfo.InvariantCulture);
        VerifyOnRead = s.VerifyChunksOnRead;
        WriteThrough = s.WriteMode == WriteMode.WriteThroughOnClose;
    }

    public override void OnActivated()
    {
        LoadSettings();
        LoadLists();
        base.OnActivated();
    }

    private int _tick;

    public override void Refresh()
    {
        var status = _host.Cache.GetStatus(0);
        UsagePercent = status.BudgetBytes == 0 ? 0 : Math.Min(100, status.PhysicalBytes * 100.0 / status.BudgetBytes);
        Summary = $"{Format.Bytes(status.PhysicalBytes)} of {Format.Bytes(status.BudgetBytes)} used on disk · free disk {Format.Bytes(status.DiskFreeBytes)} (floor {Format.Bytes(status.MinFreeBytes)})";
        if (_tick++ % 5 == 0)
        {
            LoadUsage();
        }
    }

    private void LoadUsage()
    {
        var selected = SelectedUsage?.AccountId;
        Usage.Clear();
        var usage = _host.Cache.Store.GetUsage().ToDictionary(u => u.AccountId);
        foreach (var r in _host.Accounts.Runtimes)
        {
            usage.TryGetValue(r.Id, out var u);
            Usage.Add(new CacheUsageRow(r.Id, r.Account.DisplayName, Format.Bytes(u?.PhysicalBytes ?? 0), Format.Bytes(u?.LogicalBytes ?? 0), u?.Chunks ?? 0, u?.Files ?? 0, Format.Bytes(_host.Staging.AccountBytes(r.Id))));
        }

        SelectedUsage = Usage.FirstOrDefault(u => u.AccountId == selected);
    }

    private void LoadLists()
    {
        LoadUsage();
        string Name(string? id) => id == null ? "All accounts" : _host.Accounts.Get(id)?.Account.DisplayName ?? id;
        Pins.Clear();
        foreach (var (a, p) in _host.Cache.GetPins())
        {
            Pins.Add(new PinRow(a, Name(a), p));
        }

        Exclusions.Clear();
        foreach (var (id, a, pattern) in _host.Cache.GetExclusions())
        {
            Exclusions.Add(new ExclusionRow(id, Name(a), pattern));
        }

        Scopes.Clear();
        Scopes.Add(new Choice(null, "All accounts"));
        foreach (var r in _host.Accounts.Runtimes)
        {
            Scopes.Add(new Choice(r.Id, r.Account.DisplayName));
        }

        PatternScope = Scopes[0];
    }

    private void SaveSettings()
    {
        var s = _host.Settings.Current.Clone();
        var previous = _host.Settings.Current.Clone();
        var budgetGb = IsCustom ? ParseGb(CustomBudgetGb, "Custom cache size") : double.Parse(BudgetChoice, CultureInfo.InvariantCulture);
        s.CacheBudgetBytes = (long)(budgetGb * AppSettings.GiB);
        s.MinFreeDiskBytes = (long)(ParseGb(MinFreeGb, "Minimum free space") * AppSettings.GiB);
        if (!int.TryParse(ChunkSizeMiB, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunk))
        {
            throw new FormatException("Chunk size must be a whole number of MiB.");
        }

        s.ChunkSizeMiB = chunk;
        s.VerifyChunksOnRead = VerifyOnRead;
        s.WriteMode = WriteThrough ? WriteMode.WriteThroughOnClose : WriteMode.WriteBack;
        var errors = s.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errors));
        }

        var free = _host.Cache.DiskFreeBytes();
        if (free != null && s.CacheBudgetBytes > free.Value + _host.Cache.Store.TotalPhysicalBytes - s.MinFreeDiskBytes &&
            !UiErrors.Confirm($"The cache budget ({Format.Bytes(s.CacheBudgetBytes)}) is larger than the space available while keeping {Format.Bytes(s.MinFreeDiskBytes)} free. The free-space floor always wins, so the cache will stay smaller. Save anyway?"))
        {
            return;
        }

        _ = _host.ApplySettingsAsync(s, previous);
        Status = s.ChunkSizeMiB != previous.ChunkSizeMiB
            ? "Saved. The new chunk size applies to files cached from now on (existing blocks are re-fetched when needed)."
            : "Cache settings saved.";
    }

    private static double ParseGb(string text, string field)
    {
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
        {
            throw new FormatException($"{field}: enter a number of GB.");
        }

        return v;
    }

    private void ClearSelected()
    {
        if (SelectedUsage == null)
        {
            return;
        }

        if (UiErrors.Confirm($"Clear the cached data of {SelectedUsage.Account}? Cloud files and changes waiting for upload are kept."))
        {
            _host.Cache.Store.ClearAccount(SelectedUsage.AccountId);
            _host.Cache.Metadata.InvalidateAll(SelectedUsage.AccountId);
            Status = $"Cache of {SelectedUsage.Account} cleared.";
            LoadUsage();
        }
    }

    private void ClearEverything()
    {
        if (UiErrors.Confirm("Clear all cached data? Cloud files and changes waiting for upload are kept."))
        {
            _host.Cache.ClearAllBlocks();
            Status = "All cached blocks removed.";
            LoadUsage();
        }
    }

    private async Task VerifyAsync()
    {
        Status = "Verifying every cached block…";
        var (checkedCount, removed) = await Task.Run(() => _host.Cache.Store.VerifyAll(_host.ShutdownToken));
        Status = $"Verified {checkedCount} block(s); removed {removed} missing, corrupt or orphaned item(s).";
        LoadUsage();
    }

    private async Task DownloadPinnedAsync()
    {
        if (SelectedPin == null)
        {
            return;
        }

        var provider = _host.Accounts.TryGetProvider(SelectedPin.AccountId) ?? throw new InvalidOperationException("The account is not connected.");
        var pin = SelectedPin;
        var files = new List<CloudEntry>();
        var root = await _host.Cache.Metadata.StatAsync(pin.AccountId, provider, pin.Path, _host.ShutdownToken);
        if (root == null)
        {
            throw new InvalidOperationException("The pinned path no longer exists.");
        }

        if (root.IsDirectory)
        {
            await CollectAsync(pin.AccountId, provider, pin.Path, files);
        }
        else
        {
            files.Add(root);
        }

        var total = files.Sum(f => f.Size);
        if (!UiErrors.Confirm($"Download {files.Count} file(s), {Format.Bytes(total)}, into the cache now? Pinned data counts against the cache budget."))
        {
            return;
        }

        long done = 0;
        foreach (var f in files)
        {
            var missing = _host.Cache.MissingChunks(pin.AccountId, f);
            done += await _host.Cache.PrefetchAsync(pin.AccountId, f, missing, FetchPriority.P2, _host.ShutdownToken);
            Status = $"Caching pinned data: {Format.Bytes(done)} downloaded…";
        }

        Status = $"Pinned data cached ({Format.Bytes(done)} downloaded).";
        LoadUsage();
    }

    private async Task CollectAsync(string accountId, ICloudStorageProvider provider, string path, List<CloudEntry> into)
    {
        var listing = await _host.Cache.Metadata.ListAsync(accountId, provider, path, _host.ShutdownToken);
        foreach (var e in listing.Entries)
        {
            if (e.IsDirectory)
            {
                await CollectAsync(accountId, provider, e.Path, into);
            }
            else
            {
                into.Add(e);
            }
        }
    }
}
