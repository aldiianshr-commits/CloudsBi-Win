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

    public static void ApplyTheme(AppTheme mode)
    {
        var dictionaries = Current.Resources.MergedDictionaries;
        var source = new Uri(mode == AppTheme.Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        dictionaries[0] = new ResourceDictionary { Source = source };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Host?.Log.Error("ui", "Unhandled UI exception.", e.Exception);
        Mvvm.UiErrors.Show(e.Exception, "Unexpected error");
        e.Handled = true;
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
