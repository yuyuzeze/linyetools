using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Accepts a confirmed final segment for asynchronous background translation (PROJECT.md 8.5).
/// Enqueuing must never block recognition, capture, recording, storage of the original, or the UI
/// thread. Trigger rules (ja / final / enabled / non-empty / not already translated / no active
/// job) are enforced by the implementation.
/// </summary>
public interface ITranslationQueue
{
    ValueTask EnqueueAsync(TranscriptSegment finalSegment, CancellationToken cancellationToken);
}
