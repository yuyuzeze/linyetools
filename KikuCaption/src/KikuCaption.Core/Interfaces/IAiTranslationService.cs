namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Translates a single confirmed subtitle line to the target language via the company's
/// OpenAI-compatible API (PROJECT.md 8.5). Implementations own the wire protocol and are the only
/// place that talks HTTP; callers pass plain text and never see credentials.
/// </summary>
public interface IAiTranslationService
{
    /// <summary>
    /// Translates <paramref name="text"/> from <paramref name="sourceLanguage"/> to
    /// <paramref name="targetLanguage"/>. Returns the trimmed translation; throws a typed
    /// translation exception on any failure so the queue can classify it.
    /// </summary>
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
