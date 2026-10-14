using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiagKit.Converters;

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

/// <summary>
/// For radio buttons bound to one enum or number: checked when the value equals ConverterParameter, and checking it
/// sets the value to the parameter. Saves a bool property per choice.
/// </summary>
public sealed class EqualsParameterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string p)
        {
            return Binding.DoNothing;
        }

        return targetType.IsEnum ? Enum.Parse(targetType, p, ignoreCase: true) : System.Convert.ChangeType(p, targetType, CultureInfo.InvariantCulture);
    }
}
