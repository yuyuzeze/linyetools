using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Translates a single confirmed subtitle line to the target language via the company's
/// OpenAI-compatible API (PROJECT.md 8.5). Implementations own the wire protocol and are the only
/// place that talks HTTP; callers pass plain text and never see credentials.
/// </summary>
public interface IAiTranslationService
{
    /// <summary>
    /// Translates <see cref="TranslationRequest.Text"/> in the request's direction, using the
    /// request's model and prompt version (all from the job's session snapshot — UI-R4A). Returns the
    /// trimmed translation; throws a typed translation exception on any failure so the queue can
    /// classify it. An unsupported prompt version fails as an invalid configuration before any HTTP
    /// call is made.
    /// </summary>
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
