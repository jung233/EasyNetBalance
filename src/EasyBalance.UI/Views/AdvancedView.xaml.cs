using System.Windows.Controls;
using EasyBalance.UI.ViewModels;
using Microsoft.Win32;

namespace EasyBalance.UI.Views;

public partial class AdvancedView : UserControl
{
    public AdvancedView() => InitializeComponent();

    private void BrowseSingBox_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose sing-box executable",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true && DataContext is AdvancedViewModel viewModel)
        {
            viewModel.ChooseSingBoxPath(dialog.FileName);
        }
    }
}
