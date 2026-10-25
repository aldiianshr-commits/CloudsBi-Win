using System.Windows.Controls;
using System.Windows.Input;
using CloudSBiian.Desktop.ViewModels;

namespace CloudSBiian.Desktop.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FileBrowserViewModel vm && vm.Open.CanExecute(null))
        {
            vm.Open.Execute(null);
        }
    }
}
