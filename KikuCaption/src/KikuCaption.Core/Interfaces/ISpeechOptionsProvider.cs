using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Builds the full <see cref="SpeechOptions"/> for a chosen recognition language. Shared by the
/// real-time pipeline and the WAV entry point so there is exactly one place that decides model /
/// device / compute / beam and the per-language decoding context (initial prompt + hotwords).
/// A language never receives another language's prompt/hotwords.
/// </summary>
public interface ISpeechOptionsProvider
{
    SpeechOptions ForLanguage(string language);
}
