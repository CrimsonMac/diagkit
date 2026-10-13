using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiagKit.Models;
using DiagKit.Report;
using DiagKit.ViewModels;

namespace DiagKit.Converters;

/// <summary>LedState or Severity to the matching LED brush from the theme.</summary>
public sealed class LedBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        LedState state = value switch
        {
            LedState s => s,
            Severity sev => LedMap.From(sev),
            _ => LedState.Off,
        };
        return Application.Current?.TryFindResource("Led." + state) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

public sealed class SeverityToLedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Severity s ? LedMap.From(s) : LedState.Off;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}

/// <summary>true → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert")
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Non-null and non-empty → Visible.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            null => Visibility.Collapsed,
            string s when string.IsNullOrWhiteSpace(s) => Visibility.Collapsed,
            int i when i == 0 => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
