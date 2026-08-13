using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.App.Tests;

/// <summary>A scriptable ITranscriptStore for UI-R5C App tests: answers segments + session; rest throws.</summary>
internal sealed class TestTranscriptStore : TranscriptStoreAdapter
{
    public IReadOnlyList<StoredSegment> Segments = System.Array.Empty<StoredSegment>();
    public StoredSession? Session;
    public StoredSession? MostRecent { get; set; }
    public IReadOnlyList<StoredSession> Recent = System.Array.Empty<StoredSession>();

    public override Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid s, CancellationToken c) => Task.FromResult(Segments);
    public override Task<StoredSession?> GetSessionAsync(Guid s, CancellationToken c) => Task.FromResult(Session);
    public override Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken c) => Task.FromResult(MostRecent);
    public override Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken c)
        => Task.FromResult<IReadOnlyList<StoredSession>>(Recent.Take(limit).ToArray());

}
