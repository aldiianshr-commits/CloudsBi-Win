using System.Windows;
using System.Windows.Threading;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Desktop.Dialogs;

namespace CloudSBiian.Desktop.Services;

/// <summary>
/// Automated UI smoke test used by CI ("CloudSBiian.exe --ui-selftest result.txt"): renders every
/// page in both themes and opens the Add Account dialog for every provider, then exits with
/// code 0 when no exception occurred. XAML problems only surface at runtime, so this matters.
/// </summary>
public static class UiSelfTest
{
    public static async Task RunAsync(MainWindow window, string outputFile)
    {
        var errors = App.SelfTestErrors!;
        var visited = new List<string>();
        try
        {
            foreach (var theme in new[] { Core.Configuration.AppTheme.Dark, Core.Configuration.AppTheme.Light })
            {
                App.ApplyTheme(theme);
                foreach (var page in window.ViewModel.Pages)
                {
                    window.ViewModel.CurrentPage = page;
                    await Render();
                    page.Refresh();
                    await Render();
                    visited.Add($"{theme}/{page.Title}");
                }
            }

            foreach (var kind in Enum.GetValues<ProviderKind>().Where(k => k != ProviderKind.Mock))
            {
                var dialog = new AddAccountWindow(App.Host!, App.Host!.Accounts.Catalog, kind) { ShowActivated = false };
                dialog.Show();
                await Render();
                dialog.Close();
                visited.Add($"AddAccount/{kind}");
            }

            await ExerciseMissingRcloneAsync(window, visited, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
        }

        var lines = new List<string> { errors.Count == 0 ? "OK" : "FAILED", $"Visited: {visited.Count}" };
        lines.AddRange(visited);

        // Real dependency state of this build (used by CI to prove the bundled rclone is found).
        var rclone = App.Host!.Accounts.RcloneStatus;
        lines.Add($"rclone: {rclone.State} | {rclone.Source ?? "-"} | {rclone.Version ?? "-"} | {rclone.ExecutablePath ?? "-"} | backends {rclone.BackendCount}");
        lines.Add($"rclone message: {rclone.Message}");
        foreach (var d in CloudProviders.ProviderRegistry.All)
        {
            var (status, _) = App.Host.Accounts.EffectiveStatus(d.Kind);
            lines.Add($"provider: {d.Kind} = {status}");
        }

        lines.AddRange(errors.Select(e => "ERROR: " + e));
        File.WriteAllLines(outputFile, lines);
        Application.Current.Shutdown(errors.Count == 0 ? 0 : 3);
    }

    /// <summary>
    /// Simulates "rclone not installed" (the locator returns nothing), renders the Accounts page and
    /// the Add Account dialog in that state, then restores the real locator and re-checks: rclone
    /// providers must be Blocked while missing and unblocked again afterwards.
    /// </summary>
    private static async Task ExerciseMissingRcloneAsync(MainWindow window, List<string> visited, List<string> errors)
    {
        var accounts = App.Host!.Accounts;
        var realLocate = accounts.RcloneLocate;
        var before = accounts.RcloneStatus;
        try
        {
            accounts.RcloneLocate = _ => new CloudProviders.Rclone.RcloneLocation(null, null, [], null);
            var missing = await accounts.CheckRcloneAsync(App.Host.ShutdownToken);
            await Render();
            var (drive, note) = accounts.EffectiveStatus(ProviderKind.GoogleDrive);
            visited.Add($"rclone-missing: {missing.State}, GoogleDrive {drive} ({note})");
            if (missing.State != CloudProviders.Rclone.RcloneState.Missing || drive != ImplementationStatus.Blocked)
            {
                errors.Add("Simulated missing rclone did not block rclone providers.");
            }

            var page = window.ViewModel.Pages.First(p => p is ViewModels.AccountsViewModel);
            window.ViewModel.CurrentPage = page;
            await Render();
            var dialog = new AddAccountWindow(App.Host, null, ProviderKind.GoogleDrive) { ShowActivated = false };
            dialog.Show();
            await Render();
            dialog.Close();
            visited.Add("rclone-missing: Accounts page + AddAccount/GoogleDrive rendered");
        }
        finally
        {
            accounts.RcloneLocate = realLocate;
        }

        var after = await App.Host.RecheckRcloneAsync();
        await Render();
        visited.Add($"rclone-recheck: {after.State} (before simulation: {before.State})");
        if (after.State != before.State)
        {
            errors.Add($"rclone state after re-check ({after.State}) differs from the state before the simulation ({before.State}).");
        }
    }

    private static async Task Render()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
