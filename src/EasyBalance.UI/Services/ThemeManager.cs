using System.Windows;
using System.Windows.Media;

namespace EasyBalance.UI.Services;

public static class ThemeManager
{
    public static void Apply(bool dark)
    {
        var colors = dark
            ? new Dictionary<string, string> { ["PageBrush"] = "#101827", ["SurfaceBrush"] = "#1B2638", ["TextBrush"] = "#EFF4FF", ["MutedBrush"] = "#A7B5CC", ["BorderBrush"] = "#35445C", ["AccentBrush"] = "#71A8FF", ["AccentTextBrush"] = "#101827" }
            : new Dictionary<string, string> { ["PageBrush"] = "#F5F7FB", ["SurfaceBrush"] = "#FFFFFF", ["TextBrush"] = "#172033", ["MutedBrush"] = "#5D6B82", ["BorderBrush"] = "#DDE3EB", ["AccentBrush"] = "#1769E0", ["AccentTextBrush"] = "#FFFFFF" };
        foreach (var pair in colors)
            Application.Current.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
    }
}
