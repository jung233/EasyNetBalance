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
                ["WindowBackgroundBrush"] = "#0C1422", ["SurfaceBrush"] = "#152033", ["CardBrush"] = "#19263A",
                ["SidebarBrush"] = "#09111E", ["SidebarTextBrush"] = "#D0D9E7", ["SidebarMutedTextBrush"] = "#8D9BB0",
                ["SidebarActiveBrush"] = "#22395E", ["SidebarHoverBrush"] = "#17263D", ["TextPrimaryBrush"] = "#F2F5FA",
                ["TextSecondaryBrush"] = "#B0BDD0", ["BorderBrush"] = "#2B3B52", ["AccentBrush"] = "#83A4FF",
                ["AccentSoftBrush"] = "#253A5D", ["SuccessBrush"] = "#4ED0A0", ["WarningBrush"] = "#F1BA68",
                ["DangerBrush"] = "#FF8994", ["InputBrush"] = "#111C2C", ["DataGridHeaderBrush"] = "#1D2B40"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F4F6FA", ["SurfaceBrush"] = "#FFFFFF", ["CardBrush"] = "#FFFFFF",
                ["SidebarBrush"] = "#101828", ["SidebarTextBrush"] = "#D0D5DD", ["SidebarMutedTextBrush"] = "#98A2B3",
                ["SidebarActiveBrush"] = "#263A5C", ["SidebarHoverBrush"] = "#1D2B42", ["TextPrimaryBrush"] = "#172033",
                ["TextSecondaryBrush"] = "#526075", ["BorderBrush"] = "#E1E7F0", ["AccentBrush"] = "#315FEA",
                ["AccentSoftBrush"] = "#E9EFFF", ["SuccessBrush"] = "#087A55", ["WarningBrush"] = "#91520A",
                ["DangerBrush"] = "#B42332", ["InputBrush"] = "#FFFFFF", ["DataGridHeaderBrush"] = "#F6F8FC"
            };

        foreach (var (key, color) in values)
        {
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
