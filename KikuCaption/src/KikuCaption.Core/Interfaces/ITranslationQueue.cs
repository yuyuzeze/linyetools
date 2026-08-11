using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Accepts a confirmed final segment for asynchronous background translation (PROJECT.md 8.5,
/// UI-R4A). Enqueuing must never block recognition, capture, recording, storage of the original, or
/// the UI thread. The direction comes from the immutable per-session snapshot passed in, so a job's
/// source/target are fixed for its whole lifetime (including crash recovery). Trigger rules
/// (final / effective-enabled / correct source / non-empty / not already translated / no active
/// job) are enforced by the implementation.
/// </summary>
public interface ITranslationQueue
{
    ValueTask EnqueueAsync(TranscriptSegment finalSegment, SessionTranslationOptions session, CancellationToken cancellationToken);
}
