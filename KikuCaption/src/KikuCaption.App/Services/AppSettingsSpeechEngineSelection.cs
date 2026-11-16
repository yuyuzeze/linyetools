using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Speech.SpeechCpp;

namespace KikuCaption.App.Services;

/// <summary>
/// The effective engine choice, read from <c>appsettings.json</c> (the single configuration source)
/// and AND-ed with real availability: speech.cpp is reported only when it is selected AND the backend
/// is present. That keeps the options provider and the recognizer factory in agreement, so a Whisper
/// recognizer is never handed a speech.cpp model.
///
/// The model resolves so the app stays self-contained and exe-relative: an existing <c>.gguf</c> path
/// in config wins, then a model bundled under <c>models/speech</c> (loaded by absolute path), then the
/// plain name.
/// </summary>
public sealed class AppSettingsSpeechEngineSelection : ISpeechEngineSelection
{
    private readonly SpeechSettings _settings;
    private readonly ISpeechCppEnvironment _environment;
    private readonly string? _bundledModelPath;

    public AppSettingsSpeechEngineSelection(SpeechSettings settings, ISpeechCppEnvironment environment,
        string? bundledModelPath = null)
    {
        _settings = settings;
        _environment = environment;
        _bundledModelPath = bundledModelPath;
    }

    public SpeechEngine Engine
    {
        get
        {
            var wantsSpeechCpp = string.Equals(_settings.Engine, "SpeechCpp", StringComparison.OrdinalIgnoreCase);
            return wantsSpeechCpp && _environment.IsAvailable ? SpeechEngine.SpeechCpp : SpeechEngine.Whisper;
        }
    }

    public string? SpeechCppModel
    {
        get
        {
            var configured = _settings.SpeechCppModel;

            // 1) An explicit, existing .gguf path in config wins.
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                return configured;
            }

            // 2) A model bundled next to the app (loaded by absolute path, no install needed).
            if (_bundledModelPath is not null)
            {
                return _bundledModelPath;
            }

            // 3) Otherwise the plain name; speech.cpp resolves it from its own store.
            return string.IsNullOrWhiteSpace(configured) ? "haas-ja-v1.0" : configured;
        }
    }
}
