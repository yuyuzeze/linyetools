using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.App.Tests;

/// <summary>A scriptable ITranscriptStore for UI-R5C App tests: answers segments + session; rest throws.</summary>
internal sealed class TestTranscriptStore : ITranscriptStore
{
    public IReadOnlyList<StoredSegment> Segments = System.Array.Empty<StoredSegment>();
    public StoredSession? Session;
    public StoredSession? MostRecent;

    public Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid s, CancellationToken c) => Task.FromResult(Segments);
    public Task<StoredSession?> GetSessionAsync(Guid s, CancellationToken c) => Task.FromResult(Session);
    public Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken c) => Task.FromResult(MostRecent);

    public Task InitializeAsync(CancellationToken c) => Task.CompletedTask;
    public Task<IReadOnlyList<StoredSession>> GetIncompleteSessionsAsync(CancellationToken c) => throw new NotSupportedException();
    public Task SetSessionStateAsync(Guid s, string st, DateTimeOffset? e, CancellationToken c) => throw new NotSupportedException();
    public Task SetRecordingPathAsync(Guid s, string p, CancellationToken c) => throw new NotSupportedException();
    public Task CreateSessionAsync(MeetingSession s, CancellationToken c) => throw new NotSupportedException();
    public Task UpsertSegmentAsync(TranscriptSegment s, CancellationToken c) => throw new NotSupportedException();
    public Task CompleteSessionAsync(Guid s, DateTimeOffset e, CancellationToken c) => throw new NotSupportedException();
    public Task<TranscriptSegment?> GetSegmentAsync(Guid s, CancellationToken c) => throw new NotSupportedException();
    public Task CreateTranslationJobAsync(TranslationJob j, CancellationToken c) => throw new NotSupportedException();
    public Task UpdateTranslationJobAsync(TranslationJob j, CancellationToken c) => throw new NotSupportedException();
    public Task<TranslationJob?> GetActiveJobForSegmentAsync(Guid s, CancellationToken c) => throw new NotSupportedException();
    public Task<IReadOnlyList<TranslationJob>> GetResumableJobsAsync(CancellationToken c) => throw new NotSupportedException();
    public Task<int> RecoverInProgressJobsAsync(CancellationToken c) => throw new NotSupportedException();
    public Task<IReadOnlyList<TranslationJob>> GetJobsForSessionAsync(Guid s, CancellationToken c) => throw new NotSupportedException();
    public Task SetSegmentTranslationAsync(Guid s, string? t, Core.Enums.TranscriptStatus st, CancellationToken c) => throw new NotSupportedException();
}
