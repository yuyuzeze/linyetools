using System.Windows.Data;
using System.Windows.Markup;

namespace KikuCaption.App.Localization;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:Loc Home.StartMeeting}"</c>. It binds to
/// the shared <see cref="LocalizationService.Instance"/> indexer so switching language updates the
/// text live, with no zh/en literals in views or view models (UI-R3).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    /// <summary>The resource key (see <see cref="LocalizedStrings"/>).</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
