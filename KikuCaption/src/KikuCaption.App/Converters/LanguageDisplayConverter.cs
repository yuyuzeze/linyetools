using System.Globalization;
using System.Windows.Data;
using KikuCaption.App.Localization;

namespace KikuCaption.App.Converters;

/// <summary>
/// Maps a stable internal language code (ja/zh/en) to its display endonym (日本語/中文/English) for
/// the UI, while the bound value stays the code (UI-R3 home tweak: show 日本語, not <c>ja</c>).
/// </summary>
public sealed class LanguageDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = (value as string ?? string.Empty).ToLowerInvariant();
        return string.IsNullOrEmpty(code) ? string.Empty : LocalizationService.Instance["Lang." + code];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
