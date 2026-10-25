using System.Windows;
using System.Windows.Controls;

namespace CloudSBiian.Desktop.Dialogs;

public sealed class RemoveAccountDialog : DialogBase
{
    private readonly CheckBox _deleteCredentials = new() { Content = "Also delete the stored credentials for this account", IsChecked = true };
    private readonly CheckBox _discard;

    private RemoveAccountDialog(string accountName, int pendingUploads)
        : base("Remove account", 520)
    {
        AddHeading($"Remove \"{accountName}\" from CloudSBiian?");
        AddText("Files in the cloud are NOT deleted. CloudSBiian removes its local configuration, the cached data of this account and its prediction profile. Other accounts are not affected.", muted: false);
        Body.Children.Add(_deleteCredentials);
        _discard = new CheckBox
        {
            Content = $"Discard {pendingUploads} local change(s) that were not uploaded yet",
            Visibility = pendingUploads > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        Body.Children.Add(_discard);
        if (pendingUploads > 0)
        {
            var warn = AddText("This account has changes that exist only on this PC. If you do not discard them, the account cannot be removed — upload them first from the Transfer Queue.", muted: false);
            warn.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        }

        AddButton("Cancel", (_, _) => DialogResult = false, isCancel: true);
        var remove = AddButton("Remove account", (_, _) => DialogResult = true, primary: true);
        if (pendingUploads > 0)
        {
            remove.IsEnabled = false;
            _discard.Checked += (_, _) => remove.IsEnabled = true;
            _discard.Unchecked += (_, _) => remove.IsEnabled = false;
        }
    }

    public static (bool DeleteCredentials, bool DiscardPending)? Ask(string accountName, int pendingUploads)
    {
        var d = new RemoveAccountDialog(accountName, pendingUploads);
        return d.ShowDialog() == true ? (d._deleteCredentials.IsChecked == true, d._discard.IsChecked == true) : null;
    }
}
