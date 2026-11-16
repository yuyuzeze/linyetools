using System.IO;
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
///
/// The model is resolved so the app stays self-contained: an existing file path in settings wins,
/// then a model bundled under <c>models/speech</c> (loaded by absolute path), then the plain name
/// (which speech.cpp resolves from its own store). So a user can drop a .gguf into the bundled
/// folder and need no separate speech.cpp install.
/// </summary>
public sealed class UserSettingsSpeechEngineSelection : ISpeechEngineSelection
{
    private readonly UserSettingsStore _store;
    private readonly ISpeechCppEnvironment _environment;
    private readonly string? _bundledModelPath;

    public UserSettingsSpeechEngineSelection(UserSettingsStore store, ISpeechCppEnvironment environment,
        string? bundledModelPath = null)
    {
        _store = store;
        _environment = environment;
        _bundledModelPath = bundledModelPath;
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
            var configured = _store.Load().Settings.SpeechCppModel;

            // 1) An explicit, existing file path in settings always wins.
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            // 2) A model bundled with the app (loaded by absolute path, no install needed).
            if (_bundledModelPath is not null)
            {
                return _bundledModelPath;
            }

            // 3) Otherwise the plain name; speech.cpp resolves it from its own store (or fetches it).
            return string.IsNullOrWhiteSpace(configured) ? "haas-ja-v1.0" : configured;
        }
    }
}
