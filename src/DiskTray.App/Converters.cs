using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskTray.Core.Tray;
using Wpf.Ui.Controls;

namespace DiskTray.App;

/// <summary>Turns an <see cref="Rgb"/> into a brush.</summary>
internal sealed class RgbBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Rgb rgb ? ToBrush(rgb) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static SolidColorBrush ToBrush(Rgb rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Collapses an element when its value is null, false or empty.</summary>
internal sealed class VisibleWhenSetConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or false or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an <see cref="AlertLevel"/> to an info bar's severity.</summary>
internal sealed class SeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AlertLevel.Critical => InfoBarSeverity.Error,
        AlertLevel.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
