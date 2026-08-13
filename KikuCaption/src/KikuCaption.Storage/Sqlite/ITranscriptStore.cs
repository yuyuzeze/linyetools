using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;

namespace KikuCaption.Storage.Sqlite;

/// <summary>
/// Read/admin surface over the SQLite store, used by the exporter and recovery. Extends the
/// write-only <see cref="ITranscriptRepository"/> from Core.
/// </summary>
public interface ITranscriptStore : ITranscriptRepository, ITranslationJobStore
{
    /// <summary>Opens the database, applies pragmas, and creates/validates the schema.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<StoredSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>The most recently started session, if any (for reopening a meeting's timeline).</summary>
    Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken cancellationToken);

    /// <summary>Most recently started sessions, newest first, for the home-page history browser.</summary>
    Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Final segments ordered by stable sequence number.</summary>
    Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Sessions that were never completed (candidates for recovery).</summary>
    Task<IReadOnlyList<StoredSession>> GetIncompleteSessionsAsync(CancellationToken cancellationToken);

    Task SetSessionStateAsync(Guid sessionId, string state, DateTimeOffset? endedAt, CancellationToken cancellationToken);

    /// <summary>Records the path of the session's MP4 (Milestone 5).</summary>
    Task SetRecordingPathAsync(Guid sessionId, string recordingPath, CancellationToken cancellationToken);

    // Translation-job persistence (Milestone 6) is inherited from ITranslationJobStore.
}
