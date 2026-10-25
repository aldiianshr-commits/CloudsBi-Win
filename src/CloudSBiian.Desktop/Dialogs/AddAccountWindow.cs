using System.IO;
using System.Windows;
using System.Windows.Controls;
using CloudSBiian.CloudProviders;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Accounts;
using CloudSBiian.Core.Paths;
using CloudSBiian.Desktop.Services;

namespace CloudSBiian.Desktop.Dialogs;

/// <summary>
/// Adds one account. Repeating it for the same provider adds another, fully independent account.
/// Forms for rclone-based providers are generated from the option schema reported by the rclone binary.
/// </summary>
public sealed class AddAccountWindow : DialogBase
{
    private readonly AppHost _host;
    private readonly RcloneBackendCatalog? _catalog;
    private readonly ComboBox _provider = new();
    private readonly TextBlock _statusNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _form = new();
    private readonly TextBlock _progress = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<string, Func<string>> _fields = new();
    private readonly HashSet<string> _secretFields = new();
    private readonly Button _addButton;
    private readonly CheckBox _showAdvanced = new() { Content = "Show advanced options" };
    private TextBox _name = new();
    private TextBox _root = new();
    private ComboBox? _genericBackend;
    private CancellationTokenSource? _cts;

    public AddAccountWindow(AppHost host, RcloneBackendCatalog? catalog, ProviderKind? preselect)
        : base("Add cloud account", 600)
    {
        _host = host;
        _catalog = catalog;
        AddText("Credentials are stored encrypted with Windows DPAPI for your Windows user only. CloudSBiian never sends them anywhere except the provider (directly or through the local rclone process).");
        Body.Children.Add(Label("Provider"));
        foreach (var d in ProviderRegistry.All)
        {
            _provider.Items.Add(new ComboBoxItem { Content = d.DisplayName, Tag = d });
        }

        _provider.SelectionChanged += (_, _) => BuildForm();
        Body.Children.Add(_provider);
        _statusNote.SetResourceReference(StyleProperty, "Muted");
        Body.Children.Add(_statusNote);
        Body.Children.Add(_form);
        _progress.SetResourceReference(StyleProperty, "Muted");
        Body.Children.Add(_progress);
        _showAdvanced.Checked += (_, _) => BuildForm();
        _showAdvanced.Unchecked += (_, _) => BuildForm();

        AddButton("Cancel", (_, _) =>
        {
            _cts?.Cancel();
            DialogResult = false;
        }, isCancel: true);
        _addButton = AddButton("Add account", async (_, _) => await AddAsync(), primary: true);

        var index = ProviderRegistry.All.ToList().FindIndex(d => d.Kind == (preselect ?? ProviderKind.GoogleDrive));
        _provider.SelectedIndex = Math.Max(0, index);
    }

    public CloudAccount? Added { get; private set; }

    private ProviderDescriptor Selected => (ProviderDescriptor)((ComboBoxItem)_provider.SelectedItem).Tag;

    private void BuildForm()
    {
        _form.Children.Clear();
        _fields.Clear();
        _secretFields.Clear();
        var d = Selected;
        var (status, note) = ProviderRegistry.Effective(d, _catalog);
        _statusNote.Text = $"Status: {status}. {note}";
        _addButton.IsEnabled = status != ImplementationStatus.Blocked && status != ImplementationStatus.Planned;

        var defaultName = _host.AccountStore.SuggestDisplayName(d.Kind == ProviderKind.TeraBox ? "TeraBox" : d.DisplayName);
        _name = AddTextBox("Display name", defaultName, "Shown in the app and as the folder name on the virtual drive.", _form);
        _root = AddTextBox("Root folder (optional)", "/", "Only this folder of the account is exposed. Use / for everything.", _form);

        if (status == ImplementationStatus.Blocked)
        {
            var blocked = new TextBlock { Text = "This provider cannot be added right now. See the status above and PROVIDER_SETUP.md.", TextWrapping = TextWrapping.Wrap };
            blocked.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            _form.Children.Add(blocked);
            return;
        }

        switch (d.Auth)
        {
            case ProviderAuthKind.FolderPath:
                var path = AddTextBox("Folder path", string.Empty, @"A local folder or a network share such as \\nas\games.", _form);
                var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left };
                browse.Click += (_, _) =>
                {
                    var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the storage folder" };
                    if (dlg.ShowDialog(this) == true)
                    {
                        path.Text = dlg.FolderName;
                    }
                };
                _form.Children.Add(browse);
                _fields[AccountManager.OptionPath] = () => path.Text.Trim();
                break;
            case ProviderAuthKind.OAuthBrowser:
                var info = new TextBlock
                {
                    Text = $"When you click 'Add account', your web browser opens the official {d.DisplayName} sign-in page. CloudSBiian never sees your password; it receives an access token from the provider via rclone.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 10),
                };
                _form.Children.Add(info);
                if (_showAdvanced.IsChecked == true)
                {
                    var id = AddTextBox("OAuth client ID (optional)", string.Empty, "Use your own API app to avoid shared rate limits.", _form);
                    var secret = AddPasswordBox("OAuth client secret (optional)", null, _form);
                    _fields["client_id"] = () => id.Text.Trim();
                    _fields["client_secret"] = () => secret.Password;
                    _secretFields.Add("client_secret");
                }

                _form.Children.Add(_showAdvanced);
                break;
            case ProviderAuthKind.Form when d.Kind == ProviderKind.WebDav:
                var url = AddTextBox("Server URL", "https://", "For example https://nas.local:5006/remote.php/dav/files/me/", _form);
                var user = AddTextBox("User name", string.Empty, null, _form);
                var pass = AddPasswordBox("Password", null, _form);
                _fields[AccountManager.OptionUrl] = () => url.Text.Trim();
                _fields[AccountManager.OptionUser] = () => user.Text.Trim();
                _fields[AccountManager.SecretPassword] = () => pass.Password;
                _secretFields.Add(AccountManager.SecretPassword);
                break;
            default:
                BuildRcloneForm(d);
                break;
        }
    }

    private void BuildRcloneForm(ProviderDescriptor d)
    {
        RcloneBackendInfo? backend;
        if (d.Kind == ProviderKind.GenericRclone)
        {
            if (_genericBackend == null)
            {
                _genericBackend = new ComboBox();
                foreach (var b in _catalog!.Backends.OrderBy(b => b.Name))
                {
                    _genericBackend.Items.Add(new ComboBoxItem { Content = $"{b.Name} — {b.Description}", Tag = b.Name });
                }

                _genericBackend.SelectedIndex = 0;
                _genericBackend.SelectionChanged += (_, _) => BuildForm();
            }

            _form.Children.Add(Label("rclone backend"));
            _form.Children.Add(_genericBackend);
            backend = _catalog!.Get((string)((ComboBoxItem)_genericBackend.SelectedItem).Tag);
        }
        else
        {
            backend = _catalog?.Get(d.RcloneBackend!);
        }

        if (backend == null)
        {
            return;
        }

        if (d.Kind == ProviderKind.TeraBox)
        {
            var warn = new TextBlock
            {
                Text = "TeraBox has no verified public API. This uses an unofficial rclone backend; you provide the login data it asks for (for example a cookie copied from your own browser). Each TeraBox account you add gets its own remote, session, cache and queue.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            _form.Children.Add(warn);
        }

        foreach (var option in backend.Options.Where(o => !o.Advanced || o.Required || _showAdvanced.IsChecked == true))
        {
            var label = option.Name + (option.Required ? " *" : string.Empty);
            var help = option.ShortHelp + (string.IsNullOrEmpty(option.DefaultValue) ? string.Empty : $" (default: {option.DefaultValue})");
            if (option.IsSecret)
            {
                var box = AddPasswordBox(label, help, _form);
                _fields[option.Name] = () => box.Password;
                _secretFields.Add(option.Name);
            }
            else if (option.Examples.Count > 0 && option.Exclusive)
            {
                _form.Children.Add(Label(label));
                var combo = new ComboBox();
                foreach (var ex in option.Examples)
                {
                    combo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(ex.Help) ? ex.Value : $"{ex.Value} — {ex.Help.Split('\n')[0]}", Tag = ex.Value });
                }

                combo.SelectedIndex = Math.Max(0, option.Examples.ToList().FindIndex(e => e.Value == option.DefaultValue));
                _form.Children.Add(combo);
                _fields[option.Name] = () => (string)((ComboBoxItem)combo.SelectedItem).Tag;
            }
            else
            {
                var box = AddTextBox(label, string.Empty, help, _form);
                _fields[option.Name] = () => box.Text.Trim();
            }
        }

        _form.Children.Add(_showAdvanced);
    }

    private async Task AddAsync()
    {
        var d = Selected;
        var name = _name.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _progress.Text = "Please enter a display name.";
            return;
        }

        string root;
        try
        {
            root = CloudPath.Normalize(_root.Text);
        }
        catch (PathValidationException ex)
        {
            _progress.Text = ex.Message;
            return;
        }

        var options = new Dictionary<string, string>();
        var secrets = new Dictionary<string, string>();
        foreach (var (key, getter) in _fields)
        {
            var value = getter();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (_secretFields.Contains(key))
            {
                secrets[key] = value;
            }
            else
            {
                options[key] = value;
            }
        }

        if (d.Kind == ProviderKind.LocalFolder && (!options.TryGetValue(AccountManager.OptionPath, out var p) || !Directory.Exists(p)))
        {
            _progress.Text = "The folder does not exist or is not reachable.";
            return;
        }

        string? backend = d.Kind == ProviderKind.GenericRclone ? (string?)((ComboBoxItem?)_genericBackend?.SelectedItem)?.Tag : d.RcloneBackend;
        if (d.Kind == ProviderKind.WebDav)
        {
            backend = null;
        }

        _addButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        try
        {
            if (d.Auth == ProviderAuthKind.OAuthBrowser)
            {
                _progress.Text = "Waiting for you to sign in in the browser… (Cancel stops waiting)";
                var token = await _host.Accounts.AuthorizeAsync(backend!, options.Concat(secrets).ToDictionary(k => k.Key, k => k.Value), _cts.Token);
                secrets[AccountManager.SecretToken] = token;
            }

            _progress.Text = "Connecting…";
            var account = new CloudAccount
            {
                Id = CloudAccount.NewId(d.Kind),
                Kind = d.Kind,
                DisplayName = name,
                RootPath = root,
                Backend = backend,
                Options = options,
            };
            var runtime = await _host.Accounts.AddAccountAsync(account, secrets, connect: true, _cts.Token);
            Added = account;
            if (runtime.State != AccountConnectionState.Connected)
            {
                UiMessage($"The account was added but could not connect yet:\n\n{runtime.LastError}\n\nYou can fix the problem and use Reconnect.");
            }

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _progress.Text = Core.Security.SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            _addButton.IsEnabled = true;
        }
    }

    private void UiMessage(string text) => MessageBox.Show(this, text, "CloudSBiian", MessageBoxButton.OK, MessageBoxImage.Information);
}
