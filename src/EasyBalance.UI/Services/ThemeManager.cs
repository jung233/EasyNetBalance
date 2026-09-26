using System.Windows;
using System.Windows.Media;

namespace EasyBalance.UI.Services;

public sealed class ThemeManager
{
    private bool _isDark;
    public bool IsDark => _isDark;

    public void Toggle()
    {
        _isDark = !_isDark;
        Apply();
    }

    private void Apply()
    {
        var values = _isDark
            ? new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#111824", ["SurfaceBrush"] = "#182232", ["CardBrush"] = "#1D293A",
                ["SidebarBrush"] = "#101824", ["SidebarTextBrush"] = "#DCE5F3", ["TextPrimaryBrush"] = "#EDF2F8",
                ["TextSecondaryBrush"] = "#A0AEC1", ["BorderBrush"] = "#2D3A4D", ["AccentBrush"] = "#6D94FF",
                ["AccentSoftBrush"] = "#253655", ["SuccessBrush"] = "#49C38F", ["WarningBrush"] = "#F2B35A",
                ["DangerBrush"] = "#F07883", ["InputBrush"] = "#1D293A", ["DataGridHeaderBrush"] = "#202E41"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F4F6FA", ["SurfaceBrush"] = "#FFFFFF", ["CardBrush"] = "#FFFFFF",
                ["SidebarBrush"] = "#18253A", ["SidebarTextBrush"] = "#DCE5F3", ["TextPrimaryBrush"] = "#182235",
                ["TextSecondaryBrush"] = "#68768B", ["BorderBrush"] = "#E0E6EF", ["AccentBrush"] = "#356AE6",
                ["AccentSoftBrush"] = "#E8EEFF", ["SuccessBrush"] = "#17845B", ["WarningBrush"] = "#B66A0A",
                ["DangerBrush"] = "#C33D4B", ["InputBrush"] = "#FFFFFF", ["DataGridHeaderBrush"] = "#F3F5F9"
            };

        foreach (var (key, color) in values)
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
