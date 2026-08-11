using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Settings page view model (UI-R2 interim). It currently hosts only the existing translation
/// configuration, relocated verbatim off the slimmed home page so translation stays configurable
/// without a functional regression. The full settings information architecture (常用 / 字幕 / 翻译,
/// with the UI-R4 multi-language redesign) is built in later phases; nothing new is added here.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public SettingsPageViewModel(TranslationViewModel translation) => Translation = translation;

    /// <summary>Existing translation settings panel (unchanged behaviour).</summary>
    public TranslationViewModel Translation { get; }
}
