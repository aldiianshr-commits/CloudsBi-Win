using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Desktop.Services;

namespace CloudSBiian.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static AppHost? Host { get; private set; }

    /// <summary>Set by "--ui-selftest": errors are collected instead of shown in dialogs.</summary>
    public static List<string>? SelfTestErrors { get; private set; }

    public static void ApplyTheme(AppTheme mode)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var source = new Uri(mode == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        dictionaries[0] = new ResourceDictionary { Source = source };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var selfTestIndex = Array.IndexOf(e.Args, "--ui-selftest");
        string? selfTestOutput = null;
        if (selfTestIndex >= 0)
        {
            SelfTestErrors = new List<string>();
            selfTestOutput = selfTestIndex + 1 < e.Args.Length ? e.Args[selfTestIndex + 1] : "ui-selftest.txt";
        }

        _singleInstance = new Mutex(true, @"Local\CloudSBiian.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("CloudSBiian is already running.", "CloudSBiian", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Host?.Log.Error("app", "Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Host?.Log.Warn("app", "Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        try
        {
            Host = new AppHost(AppPaths.Resolve());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"CloudSBiian could not start:\n\n{ex.Message}", "CloudSBiian", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ApplyTheme(Host.Settings.Current.Theme);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        try
        {
            await Host.StartAsync();
        }
        catch (Exception ex)
        {
            Host.Log.Error("app", "Startup sequence failed.", ex);
            SelfTestErrors?.Add("Startup: " + ex.Message);
        }

        if (selfTestOutput != null)
        {
            await Services.UiSelfTest.RunAsync(window, selfTestOutput);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Log.Error("ui", "Unhandled UI exception.", e.Exception);
        e.Handled = true;
        if (SelfTestErrors != null)
        {
            SelfTestErrors.Add(e.Exception.ToString());
            return;
        }

        Mvvm.UiErrors.Show(e.Exception, "Unexpected error");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Host != null)
        {
            try
            {
                // Unmount and flush synchronously so no WinFsp drive outlives the process.
                Task.Run(async () => await Host.DisposeAsync()).Wait(TimeSpan.FromSeconds(20));
            }
            catch (Exception)
            {
            }
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
