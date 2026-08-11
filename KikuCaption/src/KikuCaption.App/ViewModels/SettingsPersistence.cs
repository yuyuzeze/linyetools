using KikuCaption.App.ViewModels;
using KikuCaption.Infrastructure.Configuration;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Small helpers that persist individual preferences by load-merge-saving the user settings file
/// (atomic write + corrupt fallback handled by <see cref="UserSettingsStore"/>). The API key is
/// never touched here. Kept pure and store-driven so the behaviour is unit-testable (UI-R3).
/// </summary>
public static class SettingsPersistence
{
    /// <summary>Persists the confirmed meeting capture target so it is remembered across restarts.</summary>
    public static void PersistCaptureTarget(UserSettingsStore store, MeetingCaptureTarget target)
    {
        var (existing, _) = store.Load();
        store.Save(existing with
        {
            CaptureType = target.CaptureType,
            CaptureTarget = target.IsWindow ? target.WindowTitle : null
        });
    }

    /// <summary>Persists the chosen UI language.</summary>
    public static void PersistUiLanguage(UserSettingsStore store, string culture)
    {
        var (existing, _) = store.Load();
        store.Save(existing with { UiLanguage = culture });
    }
}
