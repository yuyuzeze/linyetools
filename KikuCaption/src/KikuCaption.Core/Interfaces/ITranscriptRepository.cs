using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Persists sessions and final transcript segments (PROJECT.md 8.4). Implementations use
/// parameterized SQL, enable foreign keys, and commit final segments immediately.
/// </summary>
public interface ITranscriptRepository
{
    Task CreateSessionAsync(MeetingSession session, CancellationToken cancellationToken);

    /// <summary>Idempotent upsert keyed by <see cref="TranscriptSegment.Id"/>.</summary>
    Task UpsertSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken);

    Task CompleteSessionAsync(Guid sessionId, DateTimeOffset endedAt, CancellationToken cancellationToken);
}
