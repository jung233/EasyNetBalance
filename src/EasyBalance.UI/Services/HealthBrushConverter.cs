using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyBalance.UI.Services;

public sealed class HealthBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value?.ToString() ?? string.Empty;
        var key = state.Contains("healthy", StringComparison.OrdinalIgnoreCase)
            || state.Equals("running", StringComparison.OrdinalIgnoreCase)
            ? "SuccessBrush"
            : state.Contains("down", StringComparison.OrdinalIgnoreCase)
                || state.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || state.Contains("fault", StringComparison.OrdinalIgnoreCase)
                ? "DangerBrush"
                : state.Contains("suspect", StringComparison.OrdinalIgnoreCase)
                    || state.Contains("recover", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("stopped", StringComparison.OrdinalIgnoreCase)
                    ? "WarningBrush"
                    : "TextSecondaryBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
