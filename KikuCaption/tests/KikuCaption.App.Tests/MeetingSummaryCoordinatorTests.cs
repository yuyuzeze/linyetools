using System.IO;
using System.Linq;
using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Summarization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R5C: App coordinator gating, final-only snapshot, and safe open/folder.</summary>
public class MeetingSummaryCoordinatorTests
{
    // A transcript store that only answers GetSegmentsAsync (the sole method the coordinator uses).
    private sealed class FakeStore : ITranscriptStore
    {
        public IReadOnlyList<StoredSegment> Segments = System.Array.Empty<StoredSegment>();
        public Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid s, CancellationToken c) => Task.FromResult(Segments);

        public Task InitializeAsync(CancellationToken c) => Task.CompletedTask;
        public Task<StoredSession?> GetSessionAsync(Guid s, CancellationToken c) => Task.FromResult<StoredSession?>(null);
        public Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken c) => Task.FromResult<StoredSession?>(null);
        public Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken c)
            => Task.FromResult<IReadOnlyList<StoredSession>>(Array.Empty<StoredSession>());
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
        public Task SetSegmentTranslationAsync(Guid s, string? t, TranscriptStatus st, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class FakeService : IMeetingSummaryService
    {
        public Task<MeetingSummaryResult> GenerateAsync(MeetingSummaryRequest r, string f, IProgress<MeetingSummaryProgress>? p, CancellationToken c)
            => throw new NotSupportedException();
    }

    private static StoredSegment Final(long seq, string text, string? translation = null)
        => new(new TranscriptSegment
        {
            Id = Guid.NewGuid(), SessionId = Guid.Empty, StartTime = TimeSpan.FromSeconds(seq), EndTime = TimeSpan.FromSeconds(seq + 1),
            Language = "ja", Text = text, Translation = translation,
            Status = translation is null ? TranscriptStatus.Final : TranscriptStatus.Translated,
            CreatedAt = DateTimeOffset.Now
        }, seq);

    private static StoredSegment Partial(long seq, string text)
        => new(new TranscriptSegment
        {
            Id = Guid.NewGuid(), SessionId = Guid.Empty, StartTime = TimeSpan.FromSeconds(seq), EndTime = TimeSpan.FromSeconds(seq + 1),
            Language = "ja", Text = text, Status = TranscriptStatus.Partial, CreatedAt = DateTimeOffset.Now
        }, seq);

    private static MeetingSummaryCoordinator Coordinator(FakeStore store)
        => new(store, new FakeService(), new MarkdownMeetingSummaryExporter(), NullLogger<MeetingSummaryCoordinator>.Instance);

    private static SummarySessionContext Context(SessionState state, int count, string dir = @"C:\s\1")
        => new(Guid.NewGuid(), dir, "ja", DateTimeOffset.Now, state, count);

    [Theory] // scenarios 1/2: running/starting/stopping/preflight cannot generate
    [InlineData(SessionState.Running)]
    [InlineData(SessionState.Starting)]
    [InlineData(SessionState.Stopping)]
    [InlineData(SessionState.Preflight)]
    public void CannotGenerate_WhileBusy(SessionState state)
        => Assert.False(MeetingSummaryCoordinator.CanGenerate(state, 10));

    [Theory] // scenarios 3/4: stopped current / history (idle-like) can generate when captions exist
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Completed)]
    [InlineData(SessionState.Faulted)]
    public void CanGenerate_WhenStoppedWithCaptions(SessionState state)
        => Assert.True(MeetingSummaryCoordinator.CanGenerate(state, 3));

    [Fact] // scenario 5: no final captions → cannot generate
    public void CannotGenerate_NoFinal()
        => Assert.False(MeetingSummaryCoordinator.CanGenerate(SessionState.Completed, 0));

    [Fact] // scenario 6/7/18: the snapshot uses ONLY final captions, in sequence order (no partials)
    public async Task BuildRequest_UsesFinalOnly_Ordered()
    {
        var store = new FakeStore
        {
            Segments = new[]
            {
                Final(2, "final-2"), Partial(3, "PARTIAL-TEXT"),
                Final(1, "final-1", "TRANSLATED-TEXT-MUST-NOT-BE-SENT")
            }
        };
        var req = await Coordinator(store).BuildRequestAsync(
            Context(SessionState.Completed, 2), MeetingType.SinglePresenter, "zh", "model-x", CancellationToken.None);

        Assert.Equal(2, req.Segments.Count); // partial excluded
        Assert.Equal(new[] { "final-1", "final-2" }, req.Segments.Select(s => s.Text)); // ordered by sequence
        Assert.DoesNotContain(req.Segments, s => s.Text.Contains("PARTIAL"));
        Assert.DoesNotContain(req.Segments, s => s.Text.Contains("TRANSLATED"));
        Assert.Equal("model-x", req.Model);
        Assert.Equal(MeetingSummaryPrompt.Version, req.PromptVersion);
    }

    [Fact] // building a request for a running session is rejected
    public async Task BuildRequest_Running_Throws()
    {
        var store = new FakeStore { Segments = new[] { Final(1, "x") } };
        await Assert.ThrowsAsync<MeetingSummaryException>(() =>
            Coordinator(store).BuildRequestAsync(Context(SessionState.Running, 1), MeetingType.SinglePresenter, "zh", "m", CancellationToken.None));
    }

    [Fact] // scenario 46: opening a missing summary is a safe no-op (returns false)
    public void OpenSummary_Missing_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_sum_open", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.False(Coordinator(new FakeStore()).OpenSummary(dir)); }
        finally { Directory.Delete(dir, true); }
    }
}
