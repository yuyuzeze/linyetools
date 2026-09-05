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

    /// <summary>
    /// R7B.1: resolves options for a specific model <paramref name="purpose"/> so the model path is
    /// resolved through the one shared model locator. The default delegates to
    /// <see cref="ForLanguage(string)"/> for implementations that do not care about purpose (tests).
    /// </summary>
    SpeechOptions ForLanguage(string language, WhisperModelPurpose purpose) => ForLanguage(language);
}
