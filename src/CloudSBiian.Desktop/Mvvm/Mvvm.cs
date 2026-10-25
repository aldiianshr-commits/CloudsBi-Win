using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace CloudSBiian.Desktop.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names)
        {
            OnPropertyChanged(n);
        }
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            UiErrors.Show(ex);
        }
    }
}

/// <summary>Async command: disables itself while running and reports errors in a dialog.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiErrors.Show(ex);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class UiErrors
{
    public static void Show(Exception ex, string? title = null)
    {
        var message = Core.Security.SecretRedactor.Redact(ex is AggregateException ae && ae.InnerException != null ? ae.InnerException.Message : ex.Message);
        App.Host?.Log.Warn("ui", message, ex);
        if (App.SelfTestErrors != null)
        {
            App.SelfTestErrors.Add("UiErrors: " + message);
            return;
        }

        void Display() => MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title ?? "CloudSBiian",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        if (Application.Current?.Dispatcher.CheckAccess() == true)
        {
            Display();
        }
        else
        {
            Application.Current?.Dispatcher.InvokeAsync(Display);
        }
    }

    public static bool Confirm(string message, string title = "CloudSBiian")
    {
        return MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public static void Info(string message, string title = "CloudSBiian")
    {
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>Pages refreshed once per second while visible.</summary>
public interface IRefreshable
{
    void Refresh();
}

public abstract class PageViewModel : ObservableObject, IRefreshable
{
    private string? _status;

    protected PageViewModel(string title, string icon)
    {
        Title = title;
        Icon = icon;
    }

    public string Title { get; }

    /// <summary>Segoe Fluent/MDL2 glyph.</summary>
    public string Icon { get; }

    public string? Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public virtual void Refresh()
    {
    }

    public virtual void OnActivated() => Refresh();
}
