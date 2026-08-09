using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Persistence contract for translation jobs and segment translations (M6). Defined in Core so the
/// Translation queue can depend on it without referencing Storage; the SQLite store implements it.
/// No credentials or request/response bodies are ever passed through this contract.
/// </summary>
public interface ITranslationJobStore
{
    /// <summary>Loads a segment's original text/times by id (to translate or re-translate).</summary>
    Task<TranscriptSegment?> GetSegmentAsync(Guid segmentId, CancellationToken cancellationToken);

    /// <summary>Inserts a new job. Throws if an active job already exists for the segment.</summary>
    Task CreateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken);

    /// <summary>Updates state/attempt/next-attempt/error of an existing job.</summary>
    Task UpdateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken);

    /// <summary>The active (Pending/InProgress/RetryScheduled) job for a segment, if any.</summary>
    Task<TranslationJob?> GetActiveJobForSegmentAsync(Guid segmentId, CancellationToken cancellationToken);

    /// <summary>Jobs to resume after restart (Pending or RetryScheduled), oldest first.</summary>
    Task<IReadOnlyList<TranslationJob>> GetResumableJobsAsync(CancellationToken cancellationToken);

    /// <summary>Idempotently resets lingering InProgress jobs to Pending. Returns the count.</summary>
    Task<int> RecoverInProgressJobsAsync(CancellationToken cancellationToken);

    /// <summary>All translation jobs for a session (inspection/tests).</summary>
    Task<IReadOnlyList<TranslationJob>> GetJobsForSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a segment's translation (or null on failure) and status; never touches the original
    /// text.
    /// </summary>
    Task SetSegmentTranslationAsync(Guid segmentId, string? translation, TranscriptStatus status, CancellationToken cancellationToken);
}
