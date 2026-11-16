using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Speech.SpeechCpp;

namespace KikuCaption.App.Services;

/// <summary>
/// The effective engine choice, read live from <see cref="UserSettingsStore"/> at session start and
/// AND-ed with real availability: speech.cpp is reported only when the user selected it AND the
/// backend is installed. That keeps the options provider and the recognizer factory in agreement, so
/// a Whisper recognizer is never handed a speech.cpp model. Read off the hot path (once per session).
/// </summary>
public sealed class UserSettingsSpeechEngineSelection : ISpeechEngineSelection
{
    private readonly UserSettingsStore _store;
    private readonly ISpeechCppEnvironment _environment;

    public UserSettingsSpeechEngineSelection(UserSettingsStore store, ISpeechCppEnvironment environment)
    {
        _store = store;
        _environment = environment;
    }

    public SpeechEngine Engine
    {
        get
        {
            var settings = _store.Load().Settings;
            var wantsSpeechCpp = string.Equals(settings.SpeechEngine, "SpeechCpp", StringComparison.OrdinalIgnoreCase);
            return wantsSpeechCpp && _environment.IsAvailable ? SpeechEngine.SpeechCpp : SpeechEngine.Whisper;
        }
    }

    public string? SpeechCppModel
    {
        get
        {
            var model = _store.Load().Settings.SpeechCppModel;
            return string.IsNullOrWhiteSpace(model) ? "haas-ja-v1.0" : model;
        }
    }
}
