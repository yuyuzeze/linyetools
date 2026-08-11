using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KikuCaption.App.Converters;

/// <summary>Collapses an element when its bound string is null/empty/whitespace, else shows it.</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
