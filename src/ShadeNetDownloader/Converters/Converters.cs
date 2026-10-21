using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ShadeNetDownloader.Services;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Converters;

/// <summary>AppTheme → the icon that describes it (used by the title bar theme button).</summary>
public sealed class ThemeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AppTheme.Light => SymbolRegular.WeatherSunny24,
        AppTheme.Dark => SymbolRegular.WeatherMoon24,
        _ => SymbolRegular.Desktop24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>AppTheme → tooltip text.</summary>
public sealed class ThemeNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AppTheme.Light => "Theme: Light (click for Dark)",
        AppTheme.Dark => "Theme: Dark (click to follow Windows)",
        _ => "Theme: follow Windows (click for Light)",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null / empty text → Collapsed, anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is <c>false</c> (placeholders that give way to real content).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
