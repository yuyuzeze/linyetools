using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Storage.Sqlite;

/// <summary>
/// Convenience base for focused adapters and test stores. New <see cref="ITranscriptStore"/>
/// members are centralized here, so individual fakes only override behavior relevant to a test.
/// Production persistence continues to implement the interface directly.
/// </summary>
public abstract class TranscriptStoreAdapter : ITranscriptStore
{
    protected static NotSupportedException Unsupported() => new("This store operation is not configured.");
    public virtual Task InitializeAsync(CancellationToken c) => Task.CompletedTask;
    public virtual Task CreateSessionAsync(MeetingSession s, CancellationToken c) => throw Unsupported();
    public virtual Task UpsertSegmentAsync(TranscriptSegment s, CancellationToken c) => throw Unsupported();
    public virtual Task CompleteSessionAsync(Guid id, DateTimeOffset ended, CancellationToken c) => throw Unsupported();
    public virtual Task<StoredSession?> GetSessionAsync(Guid id, CancellationToken c) => Task.FromResult<StoredSession?>(null);
    public virtual Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken c) => Task.FromResult<StoredSession?>(null);
    public virtual Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken c) => Task.FromResult<IReadOnlyList<StoredSession>>([]);
    public virtual Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid id, CancellationToken c) => Task.FromResult<IReadOnlyList<StoredSegment>>([]);
    public virtual Task<IReadOnlyList<StoredSession>> GetIncompleteSessionsAsync(CancellationToken c) => Task.FromResult<IReadOnlyList<StoredSession>>([]);
    public virtual Task DeleteSessionAsync(Guid id, CancellationToken c) => throw Unsupported();
    public virtual Task SetSessionStateAsync(Guid id, string state, DateTimeOffset? ended, CancellationToken c) => throw Unsupported();
    public virtual Task SetRecordingPathAsync(Guid id, string path, CancellationToken c) => throw Unsupported();
    public virtual Task<TranscriptSegment?> GetSegmentAsync(Guid id, CancellationToken c) => Task.FromResult<TranscriptSegment?>(null);
    public virtual Task CreateTranslationJobAsync(TranslationJob job, CancellationToken c) => throw Unsupported();
    public virtual Task UpdateTranslationJobAsync(TranslationJob job, CancellationToken c) => throw Unsupported();
    public virtual Task<TranslationJob?> GetActiveJobForSegmentAsync(Guid id, CancellationToken c) => Task.FromResult<TranslationJob?>(null);
    public virtual Task<IReadOnlyList<TranslationJob>> GetResumableJobsAsync(CancellationToken c) => Task.FromResult<IReadOnlyList<TranslationJob>>([]);
    public virtual Task<int> RecoverInProgressJobsAsync(CancellationToken c) => Task.FromResult(0);
    public virtual Task<IReadOnlyList<TranslationJob>> GetJobsForSessionAsync(Guid id, CancellationToken c) => Task.FromResult<IReadOnlyList<TranslationJob>>([]);
    public virtual Task SetSegmentTranslationAsync(Guid id, string? text, TranscriptStatus status, CancellationToken c) => throw Unsupported();
}
