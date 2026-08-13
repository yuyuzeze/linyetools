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
        About = new AboutViewModel();
    }

    public GeneralSettingsViewModel General { get; }
    public SubtitleSettingsViewModel Subtitle { get; }

    /// <summary>Existing translation settings panel (unchanged behaviour).</summary>
    public TranslationViewModel Translation { get; }
    public AboutViewModel About { get; }
}

public sealed class AboutViewModel
{
    public string VersionText
    {
        get
        {
            var version = typeof(AboutViewModel).Assembly.GetName().Version;
            return $"Version {version?.ToString(3) ?? "0.0.0"}";
        }
    }
}
