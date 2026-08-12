using System.IO;
using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R3 settings persistence: capture-target memory, UI language, and no-secret invariant.</summary>
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly UserSettingsStore _store;

    public SettingsPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kiku_r3_settings", Guid.NewGuid().ToString("N"));
        _store = new UserSettingsStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact] // confirmed window target survives a "restart" (reload from disk)
    public void PersistCaptureTarget_Window_RoundTrips()
    {
        SettingsPersistence.PersistCaptureTarget(_store, new MeetingCaptureTarget("window", "Teams 会议"));

        var (reloaded, wasReset) = _store.Load();
        Assert.False(wasReset);
        Assert.Equal("window", reloaded.CaptureType);
        Assert.Equal("Teams 会议", reloaded.CaptureTarget);
    }

    [Fact] // a screen target clears the window title
    public void PersistCaptureTarget_Screen_ClearsWindow()
    {
        SettingsPersistence.PersistCaptureTarget(_store, new MeetingCaptureTarget("window", "X"));
        SettingsPersistence.PersistCaptureTarget(_store, MeetingCaptureTarget.ScreenTarget);

        var (reloaded, _) = _store.Load();
        Assert.Equal("screen", reloaded.CaptureType);
        Assert.Null(reloaded.CaptureTarget);
    }

    [Fact] // UI language survives a restart
    public void PersistUiLanguage_RoundTrips()
    {
        SettingsPersistence.PersistUiLanguage(_store, LocalizedStrings.EnUS);

        var (reloaded, _) = _store.Load();
        Assert.Equal(LocalizedStrings.EnUS, reloaded.UiLanguage);
    }

    [Fact] // UI-R4A: translation timeout / max retries persist across a restart
    public void TranslationTimeoutAndRetries_RoundTrip()
    {
        _store.Save(new UserSettings { TranslationTimeoutSeconds = 45, TranslationMaxRetries = 5, TranslationTargetLanguage = "en" });

        var (s, _) = _store.Load();
        Assert.Equal(45, s.TranslationTimeoutSeconds);
        Assert.Equal(5, s.TranslationMaxRetries);
        Assert.Equal("en", s.TranslationTargetLanguage);
    }

    [Fact] // UI-R5A: meeting audio inputs (system / mic / stable device id) round-trip across a restart
    public void AudioInputs_RoundTrip()
    {
        _store.Save(new UserSettings { RecordSystemAudio = true, RecordMicrophone = false, MicrophoneDeviceId = "mic-endpoint-42" });

        var (s, _) = _store.Load();
        Assert.True(s.RecordSystemAudio);
        Assert.False(s.RecordMicrophone);
        Assert.Equal("mic-endpoint-42", s.MicrophoneDeviceId);
    }

    [Fact] // UI-R5A defaults: system audio on, microphone on, default communications device (null id)
    public void AudioInputs_DefaultsOn()
    {
        var s = new UserSettings();
        Assert.True(s.RecordSystemAudio);
        Assert.True(s.RecordMicrophone);
        Assert.Null(s.MicrophoneDeviceId);
    }

    [Fact] // UI-R5B: tray preferences round-trip across a restart
    public void TrayPreferences_RoundTrip()
    {
        _store.Save(new UserSettings { MinimizeToTray = false, CloseToTray = true });

        var (s, _) = _store.Load();
        Assert.False(s.MinimizeToTray);
        Assert.True(s.CloseToTray);
    }

    [Fact] // UI-R5B defaults: MinimizeToTray on, CloseToTray off (safe default)
    public void TrayPreferences_Defaults()
    {
        var s = new UserSettings();
        Assert.True(s.MinimizeToTray);
        Assert.False(s.CloseToTray);
    }

    [Fact] // the API key must never be part of the persisted settings type
    public void UserSettings_HasNoSecretProperty()
    {
        var secretish = typeof(UserSettings).GetProperties()
            .Where(p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(secretish);
    }

    [Fact] // general settings: choosing a UI language applies live and persists across restart
    public void GeneralSettings_UiLanguage_AppliesAndPersists()
    {
        var loc = new LocalizationService();
        var vm = new GeneralSettingsViewModel(_store, loc, NullLogger<GeneralSettingsViewModel>.Instance);

        vm.UiLanguage = LocalizedStrings.EnUS;

        Assert.Equal(LocalizedStrings.EnUS, loc.CurrentLanguage);      // applied live
        Assert.Equal(LocalizedStrings.EnUS, _store.Load().Settings.UiLanguage); // persisted
    }

    [Fact] // general settings save writes the non-secret fields
    public void GeneralSettings_Save_PersistsFields()
    {
        var vm = new GeneralSettingsViewModel(_store, new LocalizationService(), NullLogger<GeneralSettingsViewModel>.Instance)
        {
            DefaultRecognitionLanguage = "zh",
            LoadRecentOnStartup = true,
            DefaultRecordingTarget = "window",
            LogRetentionDays = 30
        };

        vm.SaveCommand.Execute(null);

        var (s, _) = _store.Load();
        Assert.Equal("zh", s.RecognitionLanguage);
        Assert.True(s.LoadRecentOnStartup);
        Assert.Equal("window", s.CaptureType);
        Assert.Equal(30, s.LogRetentionDays);
    }
}
