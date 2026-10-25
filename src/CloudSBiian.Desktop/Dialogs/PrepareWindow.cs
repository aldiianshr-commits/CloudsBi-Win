using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudSBiian.Desktop.Mvvm;
using CloudSBiian.Desktop.Services;
using CloudSBiian.GameLauncher;

namespace CloudSBiian.Desktop.Dialogs;

/// <summary>Optimize &amp; Prepare: shows the readiness checks and runs the (cancellable) preparation.</summary>
public sealed class PrepareWindow : DialogBase
{
    private readonly AppHost _host;
    private readonly LibraryEntry _entry;
    private readonly StackPanel _checks = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _progressText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Maximum = 1, Margin = new Thickness(0, 6, 0, 10), Visibility = Visibility.Collapsed };
    private readonly Button _prefetch;
    private readonly Button _install;
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;
    private PrepareReport? _report;

    public PrepareWindow(AppHost host, LibraryEntry entry, bool installMode)
        : base($"Optimize & Prepare — {entry.Name}", 680)
    {
        _host = host;
        _entry = entry;
        AddText("Checks connection, credentials, files, space, pending transfers and known compatibility issues. Nothing is downloaded until you choose an action.");
        Body.Children.Add(_checks);
        Body.Children.Add(_summary);
        Body.Children.Add(_progress);
        _progressText.SetResourceReference(StyleProperty, "Muted");
        Body.Children.Add(_progressText);

        _cancel = AddButton("Cancel operation", (_, _) => _cts?.Cancel());
        _cancel.IsEnabled = false;
        _install = AddButton("Install locally…", async (_, _) => await InstallAsync());
        _prefetch = AddButton("Prefetch recorded data", async (_, _) => await PrefetchAsync(), primary: !installMode);
        AddButton("Close", (_, _) =>
        {
            _cts?.Cancel();
            Close();
        }, isCancel: true);
        _prefetch.IsEnabled = false;
        _install.IsEnabled = entry.IsCloudBased;
        Loaded += async (_, _) => await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        _cts = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        try
        {
            var progress = new Progress<PrepareProgress>(p => _progressText.Text = p.Stage + "…");
            _report = await _host.Prepare.AnalyzeAsync(_entry, progress, _cts.Token);
            Render(_report);
        }
        catch (OperationCanceledException)
        {
            _progressText.Text = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            _progressText.Text = Core.Security.SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            _cancel.IsEnabled = false;
        }
    }

    private void Render(PrepareReport report)
    {
        _checks.Children.Clear();
        foreach (var c in report.Checks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, c.Severity switch
            {
                CheckSeverity.Ok => "OkBrush",
                CheckSeverity.Info => "MutedBrush",
                CheckSeverity.Warning => "WarnBrush",
                _ => "ErrorBrush",
            });
            row.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{c.Name}: {c.Message}", TextWrapping = TextWrapping.Wrap });
            if (c.Remedy != null)
            {
                var remedy = new TextBlock { Text = "→ " + c.Remedy, TextWrapping = TextWrapping.Wrap };
                remedy.SetResourceReference(StyleProperty, "Muted");
                text.Children.Add(remedy);
            }

            row.Children.Add(text);
            _checks.Children.Add(row);
        }

        var estimate = report.EstimatedSeconds == null ? "no estimate" : $"about {Format.Duration(report.EstimatedSeconds)} (approximate)";
        _summary.Text = report.GameFiles == 0
            ? string.Empty
            : $"Folder: {report.GameFiles} files, {Format.Bytes(report.GameSizeBytes)}. Recorded launch data: {Format.Bytes(report.BytesAlreadyCached)} cached, {Format.Bytes(report.BytesToPrefetch)} to prefetch — {estimate}. {report.EstimateNote}";
        _prefetch.IsEnabled = report.BytesToPrefetch > 0 && !report.HasErrors;
        _progressText.Text = report.HasErrors ? "Fix the errors above before launching." : "Ready.";
    }

    private async Task PrefetchAsync()
    {
        if (_report == null)
        {
            return;
        }

        if (_report.BytesToPrefetch > _host.Settings.Current.PrepareConfirmThresholdBytes &&
            MessageBox.Show(this, $"This prefetches {Format.Bytes(_report.BytesToPrefetch)} into the cache. Continue?", "CloudSBiian", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async (progress, ct) =>
        {
            var bytes = await _host.Prepare.PrefetchTraceAsync(_entry, progress, ct);
            return $"Prepared: {Format.Bytes(bytes)} prefetched.";
        });
        await AnalyzeAsync();
    }

    private async Task InstallAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the local folder for this game/app",
            InitialDirectory = _entry.LocalPath ?? _host.Settings.Current.DefaultFullLocalDirectory ?? string.Empty,
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        var target = System.IO.Path.Combine(dlg.FolderName, SanitizeFolder(_entry.Name));
        var size = _report?.GameSizeBytes ?? 0;
        if (MessageBox.Show(this, $"Copy {Format.Bytes(size)} to {target} and verify every file? The entry switches to Full Local mode afterwards.",
                "CloudSBiian", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(async (progress, ct) =>
        {
            await _host.Prepare.InstallLocalAsync(_entry, target, progress, ct);
            _entry.Mode = LaunchMode.FullLocal;
            _host.Library.Save(_entry);
            return $"Installed and verified at {target}.";
        });
        await AnalyzeAsync();
    }

    private async Task RunAsync(Func<IProgress<PrepareProgress>, CancellationToken, Task<string>> work)
    {
        _cts = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        _prefetch.IsEnabled = false;
        _install.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        var started = DateTime.UtcNow;
        var progress = new Progress<PrepareProgress>(p =>
        {
            _progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var rate = elapsed > 2 ? p.Done / elapsed : 0;
            var eta = rate > 0 && p.Total > p.Done ? $" · about {Format.Duration((p.Total - p.Done) / rate)} left (estimate)" : string.Empty;
            _progressText.Text = $"{p.Stage} — {Format.Bytes(p.Done)} / {Format.Bytes(p.Total)}{eta}";
        });
        try
        {
            _progressText.Text = await work(progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _progressText.Text = "Cancelled. Already cached or copied data is kept.";
        }
        catch (Exception ex)
        {
            _progressText.Text = "Failed: " + Core.Security.SecretRedactor.Redact(ex.Message);
        }
        finally
        {
            _cancel.IsEnabled = false;
            _install.IsEnabled = _entry.IsCloudBased;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private static string SanitizeFolder(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "Game" : s;
    }
}
