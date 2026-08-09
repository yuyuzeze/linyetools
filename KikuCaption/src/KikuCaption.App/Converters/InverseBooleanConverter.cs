using System.Globalization;
using System.Windows.Data;

namespace KikuCaption.App.Converters;

/// <summary>Inverts a boolean value for XAML bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
