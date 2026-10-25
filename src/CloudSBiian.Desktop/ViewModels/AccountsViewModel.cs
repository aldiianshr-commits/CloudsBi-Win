using System.Collections.ObjectModel;
using System.Text;
using CloudSBiian.CloudProviders;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Security;
using CloudSBiian.Desktop.Dialogs;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.Monitoring;

namespace CloudSBiian.Desktop.ViewModels;

public sealed class AccountRow : ObservableObject
{
    private string _name = string.Empty;
    private string _state = string.Empty;
    private string _detail = string.Empty;
    private string _quota = string.Empty;
    private string _activity = string.Empty;

    public AccountRow(string id, string provider, string providerStatus)
    {
        Id = id;
        Provider = provider;
        ProviderStatus = providerStatus;
    }

    public string Id { get; }

    public string Provider { get; }

    public string ProviderStatus { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string State { get => _state; set => SetProperty(ref _state, value); }

    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }

    public string Quota { get => _quota; set => SetProperty(ref _quota, value); }

    public string Activity { get => _activity; set => SetProperty(ref _activity, value); }
}

public sealed record ProviderStatusRow(string Name, string Status, string Note);

public sealed class AccountsViewModel : PageViewModel
{
    private readonly AppHost _host;
    private AccountRow? _selected;

    public AccountsViewModel(AppHost host)
        : base("Cloud Accounts", "")
    {
        _host = host;
        AddAccount = new AsyncCommand(() => AddAsync(null));
        AddTeraBox = new AsyncCommand(() => AddAsync(ProviderKind.TeraBox));
        Connect = new AsyncCommand(() => RunOnSelected(id => _host.Accounts.ConnectAsync(id, _host.ShutdownToken), "Connected."), HasSelection);
        Disconnect = new AsyncCommand(() => RunOnSelected(id => _host.Accounts.DisconnectAsync(id), "Disconnected."), HasSelection);
        Reconnect = new AsyncCommand(() => RunOnSelected(id => _host.Accounts.ReconnectAsync(id, reauthorize: NeedsReauth(id), _host.ShutdownToken), "Reconnected."), HasSelection);
        RefreshAccount = new AsyncCommand(() => RunOnSelected(id => _host.Accounts.RefreshAsync(id, _host.ShutdownToken), "Refreshed."), HasSelection);
        Test = new AsyncCommand(TestAsync, HasSelection);
        Rename = new RelayCommand(RenameSelected, HasSelection);
        Remove = new AsyncCommand(RemoveAsync, HasSelection);
        Details = new RelayCommand(ShowDetails, HasSelection);
        ViewTransfers = new RelayCommand(() =>
        {
            var transfers = MainWindow.Current?.Page<TransfersViewModel>();
            if (transfers != null && Selected != null)
            {
                transfers.FilterAccountId = Selected.Id;
                MainWindow.Current!.Navigate<TransfersViewModel>();
            }
        }, HasSelection);
        RefreshProviders();
    }

    public ObservableCollection<AccountRow> Accounts { get; } = new();

    public ObservableCollection<ProviderStatusRow> Providers { get; } = new();

    public AccountRow? Selected { get => _selected; set => SetProperty(ref _selected, value); }

    public AsyncCommand AddAccount { get; }

    public AsyncCommand AddTeraBox { get; }

    public AsyncCommand Connect { get; }

    public AsyncCommand Disconnect { get; }

    public AsyncCommand Reconnect { get; }

    /// <summary>"Refresh Account": reloads quota and identity.</summary>
    public AsyncCommand RefreshAccount { get; }

    public AsyncCommand Test { get; }

    public RelayCommand Rename { get; }

    public AsyncCommand Remove { get; }

    public RelayCommand Details { get; }

    public RelayCommand ViewTransfers { get; }

    private bool HasSelection() => Selected != null;

    private bool NeedsReauth(string id)
    {
        var r = _host.Accounts.Get(id);
        return r != null && r.Descriptor.Auth == ProviderAuthKind.OAuthBrowser &&
               r.State is AccountConnectionState.AuthenticationExpired or AccountConnectionState.AuthenticationFailed;
    }

    public override void OnActivated()
    {
        RefreshProviders();
        base.OnActivated();
    }

    public override void Refresh()
    {
        var runtimes = _host.Accounts.Runtimes;
        foreach (var gone in Accounts.Where(a => runtimes.All(r => r.Id != a.Id)).ToList())
        {
            Accounts.Remove(gone);
        }

        var active = _host.Transfers.Active;
        foreach (var r in runtimes)
        {
            var row = Accounts.FirstOrDefault(a => a.Id == r.Id);
            if (row == null)
            {
                row = new AccountRow(r.Id, r.Descriptor.DisplayName, _host.Accounts.EffectiveStatus(r.Account.Kind).Status.ToString());
                Accounts.Add(row);
            }

            row.Name = r.Account.DisplayName;
            row.State = r.State.ToString();
            var identity = string.IsNullOrEmpty(r.Account.Identity) ? string.Empty : SecretRedactor.MaskIdentity(r.Account.Identity) + " · ";
            row.Detail = identity + (r.LastError ?? (r.ConnectedAt is { } c ? $"connected since {c:HH:mm}" : "not connected"));
            row.Quota = r.Quota == null ? "Quota: N/A" : $"{Format.Bytes(r.Quota.Used)} used of {Format.Bytes(r.Quota.Total)}";
            var down = active.Count(t => t.AccountId == r.Id && t.Direction != TransferDirection.Upload);
            var up = active.Count(t => t.AccountId == r.Id && t.Direction == TransferDirection.Upload);
            row.Activity = down + up == 0 ? "idle" : $"{down} ↓ · {up} ↑";
        }
    }

    private void RefreshProviders()
    {
        Providers.Clear();
        foreach (var d in ProviderRegistry.All)
        {
            var (status, note) = _host.Accounts.EffectiveStatus(d.Kind);
            Providers.Add(new ProviderStatusRow(d.DisplayName, status.ToString(), note));
        }
    }

    private async Task AddAsync(ProviderKind? preselect)
    {
        Status = "Checking rclone…";
        CloudProviders.Rclone.RcloneBackendCatalog? catalog = null;
        try
        {
            catalog = await _host.Accounts.GetCatalogAsync(_host.ShutdownToken);
        }
        catch (Exception ex)
        {
            _host.Log.Warn("accounts", "rclone catalog unavailable.", ex);
        }

        Status = null;
        RefreshProviders();
        var window = new AddAccountWindow(_host, catalog, preselect);
        if (window.ShowDialog() == true && window.Added != null)
        {
            Status = $"Added {window.Added.DisplayName}.";
            Refresh();
            Selected = Accounts.FirstOrDefault(a => a.Id == window.Added.Id);
        }
    }

    private async Task RunOnSelected(Func<string, Task> action, string done)
    {
        if (Selected == null)
        {
            return;
        }

        var name = Selected.Name;
        Status = $"{name}: working…";
        try
        {
            await action(Selected.Id);
            var r = _host.Accounts.Get(Selected.Id);
            Status = r?.LastError != null ? $"{name}: {r.LastError}" : $"{name}: {done}";
        }
        catch (Exception ex)
        {
            Status = $"{name}: {SecretRedactor.Redact(ex.Message)}";
        }

        Refresh();
    }

    private async Task TestAsync()
    {
        if (Selected == null)
        {
            return;
        }

        Status = $"Testing {Selected.Name}…";
        var result = await _host.Accounts.TestAsync(Selected.Id, _host.ShutdownToken);
        Status = $"{Selected.Name}: {result.Status} — {result.Message} ({result.Latency.TotalMilliseconds:F0} ms)";
    }

    private void RenameSelected()
    {
        if (Selected == null)
        {
            return;
        }

        var name = InputDialog.Ask("Rename account", "New display name", Selected.Name);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _host.Accounts.Rename(Selected.Id, name);
            Refresh();
        }
    }

    private async Task RemoveAsync()
    {
        if (Selected == null)
        {
            return;
        }

        var pending = _host.Uploads.GetAll(Selected.Id).Count;
        var answer = RemoveAccountDialog.Ask(Selected.Name, pending);
        if (answer == null)
        {
            return;
        }

        var name = Selected.Name;
        await _host.RemoveAccountAsync(Selected.Id, answer.Value.DeleteCredentials, answer.Value.DiscardPending);
        Status = $"{name} removed (cloud files untouched).";
        Selected = null;
        Refresh();
    }

    private void ShowDetails()
    {
        var r = Selected == null ? null : _host.Accounts.Get(Selected.Id);
        if (r == null)
        {
            return;
        }

        var (status, note) = _host.Accounts.EffectiveStatus(r.Account.Kind);
        var sb = new StringBuilder();
        sb.AppendLine($"Name:            {r.Account.DisplayName}");
        sb.AppendLine($"Internal id:     {r.Id}");
        sb.AppendLine($"Provider:        {r.Descriptor.DisplayName} ({status})");
        sb.AppendLine($"Backend:         {r.Account.Backend ?? "native"}");
        sb.AppendLine($"Identity:        {(string.IsNullOrEmpty(r.Account.Identity) ? "not reported by backend" : SecretRedactor.MaskIdentity(r.Account.Identity))}");
        sb.AppendLine($"Root folder:     {r.Account.RootPath}");
        sb.AppendLine($"State:           {r.State}");
        sb.AppendLine($"Last error:      {r.LastError ?? "—"}");
        sb.AppendLine($"Connected at:    {Format.Time(r.ConnectedAt)}");
        sb.AppendLine($"Quota:           {(r.Quota == null ? "not reported" : $"used {Format.Bytes(r.Quota.Used)}, free {Format.Bytes(r.Quota.Free)}, total {Format.Bytes(r.Quota.Total)}")}");
        sb.AppendLine($"Capabilities:    {r.Provider?.Capabilities.ToString() ?? "unknown until connected"}");
        sb.AppendLine($"Last test:       {(r.LastTest == null ? "—" : $"{r.LastTest.Status}: {r.LastTest.Message} ({r.LastTest.Latency.TotalMilliseconds:F0} ms)")}");
        sb.AppendLine($"Cached data:     {Format.Bytes(_host.Cache.Store.PhysicalBytes(r.Id))} on disk");
        sb.AppendLine($"Pending uploads: {_host.Uploads.GetAll(r.Id).Count}");
        sb.AppendLine($"Credentials:     {(_host.Secrets.HasAny(r.Id) ? "stored (DPAPI, hidden)" : "none")}");
        sb.AppendLine();
        sb.AppendLine("Settings (secrets are never shown):");
        foreach (var (k, v) in r.Account.Options)
        {
            sb.AppendLine($"  {k} = {SecretRedactor.Redact(v)}");
        }

        sb.AppendLine();
        sb.AppendLine(note);
        TextReportDialog.Show($"Account details — {r.Account.DisplayName}", sb.ToString());
    }
}
