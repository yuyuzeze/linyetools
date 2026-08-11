using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace KikuCaption.App.Localization;

/// <summary>
/// XAML markup extension that shows a language code's localized display name and refreshes live on
/// a UI-language switch: <c>Text="{loc:LangName}"</c> (binds the current item, a ja/zh/en code) or
/// <c>Text="{loc:LangName Path=Realtime.SelectedLanguage}"</c>. The bound value stays the code.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LangNameExtension : MarkupExtension
{
    public LangNameExtension() { }

    public LangNameExtension(string path) => Path = path;

    /// <summary>Binding path to the language code. Defaults to the current data-context item.</summary>
    public string Path { get; set; } = ".";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var multi = new MultiBinding { Converter = LangNameConverter.Instance, Mode = BindingMode.OneWay };
        multi.Bindings.Add(new Binding(Path));
        // The second value is only a change trigger, so switching language re-evaluates the display.
        multi.Bindings.Add(new Binding(nameof(LocalizationService.CurrentLanguage)) { Source = LocalizationService.Instance });
        return multi.ProvideValue(serviceProvider);
    }
}

/// <summary>Resolves [code, currentLanguage] → localized language name via the localization service.</summary>
public sealed class LangNameConverter : IMultiValueConverter
{
    public static LangNameConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var code = (values.Length > 0 ? values[0] as string : null) ?? string.Empty;
        return string.IsNullOrEmpty(code) ? string.Empty : LocalizationService.Instance["Lang." + code.ToLowerInvariant()];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
