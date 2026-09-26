using System.Windows;
using System.Windows.Threading;
using EasyBalance.UI.ViewModels;

namespace EasyBalance.UI;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _dashboardRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        _dashboardRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _dashboardRefreshTimer.Tick += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel && viewModel.IsDashboardActive)
            {
                await viewModel.RefreshDashboardAsync();
            }
        };
        Loaded += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
            _dashboardRefreshTimer.Start();
        };
        Closed += (_, _) => _dashboardRefreshTimer.Stop();
    }
}
