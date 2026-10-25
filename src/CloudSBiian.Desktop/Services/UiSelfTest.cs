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
        }
        catch (Exception ex)
        {
            errors.Add(ex.ToString());
        }

        var lines = new List<string> { errors.Count == 0 ? "OK" : "FAILED", $"Visited: {visited.Count}" };
        lines.AddRange(visited);
        lines.AddRange(errors.Select(e => "ERROR: " + e));
        File.WriteAllLines(outputFile, lines);
        Application.Current.Shutdown(errors.Count == 0 ? 0 : 3);
    }

    private static async Task Render()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
