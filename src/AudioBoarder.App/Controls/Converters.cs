using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AudioBoarder.App.Health;

namespace AudioBoarder.App.Controls;

public sealed class HealthStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value is ComponentStatus s ? s : ComponentStatus.Unknown;
        return status switch
        {
            ComponentStatus.Ready => new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)),    // emerald
            ComponentStatus.Checking => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // amber
            ComponentStatus.Degraded => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // amber
            ComponentStatus.Failed => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),   // red
            _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),                        // grey
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "False|True").Split('|');
        return value is true ? (parts.Length > 1 ? parts[1] : "True") : parts[0];
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
