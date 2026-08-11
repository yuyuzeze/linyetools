using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Settings page view model (UI-R3). Hosts the three settings sections shown as tabs: General,
/// Subtitle (with live preview) and Translation. Each section owns its own state and persistence;
/// this type only composes them.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public SettingsPageViewModel(
        GeneralSettingsViewModel general,
        SubtitleSettingsViewModel subtitle,
        TranslationViewModel translation)
    {
        General = general;
        Subtitle = subtitle;
        Translation = translation;
    }

    public GeneralSettingsViewModel General { get; }
    public SubtitleSettingsViewModel Subtitle { get; }

    /// <summary>Existing translation settings panel (unchanged behaviour).</summary>
    public TranslationViewModel Translation { get; }
}
