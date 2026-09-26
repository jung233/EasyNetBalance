using System.Windows.Controls;
using EasyBalance.UI.ViewModels;
using Microsoft.Win32;

namespace EasyBalance.UI.Views;

public partial class RulesView : UserControl
{
    public RulesView() => InitializeComponent();

    private void BrowseExecutable_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an application executable",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true && DataContext is RulesViewModel viewModel)
        {
            viewModel.SetExecutablePath(dialog.FileName);
        }
    }
}
