using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace KikuCaption.App.Localization;

/// <summary>
/// XAML markup extension that shows the localized display of an internal value while keeping the
/// value itself unchanged, refreshing live on a UI-language switch (UI-R3.1). The resource key is
/// <c>Prefix + value</c> — e.g. <c>{loc:LocValue Prefix=Capture.}</c> maps the item "screen" to
/// <c>Capture.screen</c> ("整个屏幕" / "Entire screen" / "画面全体"); the bound value stays "screen".
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocValueExtension : MarkupExtension
{
    public LocValueExtension() { }

    public LocValueExtension(string prefix) => Prefix = prefix;

    /// <summary>Resource-key prefix (e.g. "Capture." or "Auth.").</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Binding path to the internal value. Defaults to the current data-context item.</summary>
    public string Path { get; set; } = ".";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var multi = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = new LocValueConverter(Prefix)
        };
        multi.Bindings.Add(new Binding(Path));
        multi.Bindings.Add(new Binding(nameof(LocalizationService.CurrentLanguage)) { Source = LocalizationService.Instance });
        return multi.ProvideValue(serviceProvider);
    }
}

/// <summary>Resolves [value, currentLanguage] → localized <c>Prefix + value</c> display text.</summary>
public sealed class LocValueConverter : IMultiValueConverter
{
    private readonly string _prefix;

    public LocValueConverter(string prefix) => _prefix = prefix;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var value = (values.Length > 0 ? values[0]?.ToString() : null) ?? string.Empty;
        return string.IsNullOrEmpty(value) ? string.Empty : LocalizationService.Instance[_prefix + value];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
