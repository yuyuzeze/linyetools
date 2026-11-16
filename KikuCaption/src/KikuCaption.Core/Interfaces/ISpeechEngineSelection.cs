using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// The user's currently-effective recognition engine choice, read live at session start. An
/// implementation returns <see cref="SpeechEngine.SpeechCpp"/> only when the user selected it AND
/// the backend is actually available, so the options provider and the recognizer factory always
/// agree (no mismatch where a Whisper recognizer is handed a speech.cpp model).
/// </summary>
public interface ISpeechEngineSelection
{
    SpeechEngine Engine { get; }

    /// <summary>The speech.cpp model to use when <see cref="Engine"/> is SpeechCpp (e.g. "haas-ja-v1.0").</summary>
    string? SpeechCppModel { get; }
}
