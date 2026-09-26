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
                ["WindowBackgroundBrush"] = "#0F1726", ["SurfaceBrush"] = "#19263A", ["CardBrush"] = "#19263A",
                ["SidebarBrush"] = "#0A1322", ["SidebarTextBrush"] = "#D7E0ED", ["SidebarMutedTextBrush"] = "#98A9C0",
                ["SidebarActiveBrush"] = "#25466D", ["SidebarHoverBrush"] = "#172D49", ["TextPrimaryBrush"] = "#F2F6FB",
                ["TextSecondaryBrush"] = "#B5C3D6", ["BorderBrush"] = "#34465F", ["AccentBrush"] = "#9DC0FF", ["AccentButtonTextBrush"] = "#0B1C36",
                ["AccentSoftBrush"] = "#253A5D", ["SuccessBrush"] = "#4ED0A0", ["WarningBrush"] = "#F1BA68",
                ["DangerBrush"] = "#FF8994", ["InputBrush"] = "#111C2C", ["DataGridHeaderBrush"] = "#1D2B40"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F5F7FB", ["SurfaceBrush"] = "#FFFFFF", ["CardBrush"] = "#FFFFFF",
                ["SidebarBrush"] = "#111D32", ["SidebarTextBrush"] = "#D0D5DD", ["SidebarMutedTextBrush"] = "#98A2B3",
                ["SidebarActiveBrush"] = "#29466D", ["SidebarHoverBrush"] = "#1C3150", ["TextPrimaryBrush"] = "#17243B",
                ["TextSecondaryBrush"] = "#596B83", ["BorderBrush"] = "#E1E7F0", ["AccentBrush"] = "#245DE0", ["AccentButtonTextBrush"] = "#FFFFFF",
                ["AccentSoftBrush"] = "#E9EFFF", ["SuccessBrush"] = "#087A55", ["WarningBrush"] = "#91520A",
                ["DangerBrush"] = "#B42332", ["InputBrush"] = "#FFFFFF", ["DataGridHeaderBrush"] = "#F6F8FC"
            };

        foreach (var (key, color) in values)
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
