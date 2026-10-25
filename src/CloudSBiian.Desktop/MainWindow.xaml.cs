using System.Windows;
using CloudSBiian.Desktop.ViewModels;

namespace CloudSBiian.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(App.Host!);
        DataContext = ViewModel;
    }

    public MainViewModel ViewModel { get; }

    public static MainViewModel? Current => (Application.Current?.MainWindow as MainWindow)?.ViewModel;
}
