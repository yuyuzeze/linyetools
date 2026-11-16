using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Chooses the recognizer implementation for a session from <see cref="SpeechOptions.Engine"/>.
/// Falls back to the Whisper backend when the requested engine is unavailable, so a missing
/// speech.cpp install never breaks recognition — it only logs and degrades.
/// </summary>
public interface ISpeechRecognizerFactory
{
    ISpeechRecognizer Create(SpeechOptions options);
}
