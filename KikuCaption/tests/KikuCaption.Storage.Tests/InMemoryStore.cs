using System.Collections.Concurrent;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Core.Exceptions;

namespace KikuCaption.Storage.Tests;

/// <summary>In-memory <see cref="ITranscriptStore"/> for fast, deterministic recorder tests.</summary>
internal sealed class InMemoryStore : TranscriptStoreAdapter
{
    private readonly ConcurrentDictionary<Guid, (MeetingSession Session, string State, DateTimeOffset? Ended)> _sessions = new();
    private readonly ConcurrentDictionary<Guid, List<(TranscriptSegment Seg, long Seq)>> _segments = new();
    private readonly ConcurrentDictionary<Guid, TranslationJob> _jobs = new();

    public int UpsertDelayMs { get; set; }
    public bool FailOnUpsert { get; set; }

    public override Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task CreateSessionAsync(MeetingSession session, CancellationToken cancellationToken)
    {
        _sessions.TryAdd(session.Id, (session, SessionStates.Running, session.EndedAt));
        _segments.TryAdd(session.Id, new List<(TranscriptSegment, long)>());
        return Task.CompletedTask;
    }

    public override async Task UpsertSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
    {
        if (UpsertDelayMs > 0)
        {
            await Task.Delay(UpsertDelayMs, cancellationToken);
        }

        if (FailOnUpsert)
        {
            throw new InvalidOperationException("simulated write failure");
        }

        var list = _segments.GetOrAdd(segment.SessionId, _ => new List<(TranscriptSegment, long)>());
        lock (list)
        {
            int existing = list.FindIndex(x => x.Seg.Id == segment.Id);
            if (existing >= 0)
            {
                list[existing] = (segment, list[existing].Seq);
            }
            else
            {
                list.Add((segment, list.Count + 1));
            }
        }
    }

    public override Task CompleteSessionAsync(Guid sessionId, DateTimeOffset endedAt, CancellationToken cancellationToken)
        => SetSessionStateAsync(sessionId, SessionStates.Completed, endedAt, cancellationToken);

    public override Task SetSessionStateAsync(Guid sessionId, string state, DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var current))
        {
            _sessions[sessionId] = (current.Session, state, endedAt ?? current.Ended);
        }

        return Task.CompletedTask;
    }

    public override Task SetRecordingPathAsync(Guid sessionId, string recordingPath, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var current))
        {
            _sessions[sessionId] = (current.Session with { RecordingPath = recordingPath }, current.State, current.Ended);
        }

        return Task.CompletedTask;
    }

    public override Task<StoredSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var current))
        {
            return Task.FromResult<StoredSession?>(null);
        }

        var session = current.Session with { EndedAt = current.Ended };
        int count = _segments.TryGetValue(sessionId, out var list) ? list.Count : 0;
        return Task.FromResult<StoredSession?>(new StoredSession(session, current.State, count));
    }

    public override Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken cancellationToken)
    {
        var latest = _sessions.Values
            .OrderByDescending(x => x.Session.StartedAt)
            .Select(x => new StoredSession(x.Session with { EndedAt = x.Ended }, x.State,
                _segments.TryGetValue(x.Session.Id, out var l) ? l.Count : 0))
            .FirstOrDefault();
        return Task.FromResult<StoredSession?>(latest);
    }

    public override Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredSession> result = _sessions.Values
            .OrderByDescending(x => x.Session.StartedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => new StoredSession(x.Session with { EndedAt = x.Ended }, x.State,
                _segments.TryGetValue(x.Session.Id, out var l) ? l.Count : 0))
            .ToList();
        return Task.FromResult(result);
    }

    public override Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_segments.TryGetValue(sessionId, out var list))
        {
            return Task.FromResult<IReadOnlyList<StoredSegment>>(Array.Empty<StoredSegment>());
        }

        lock (list)
        {
            IReadOnlyList<StoredSegment> result = list.OrderBy(x => x.Seq)
                .Select(x => new StoredSegment(x.Seg, x.Seq)).ToList();
            return Task.FromResult(result);
        }
    }

    public override Task<IReadOnlyList<StoredSession>> GetIncompleteSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredSession> result = _sessions.Values
            .Where(x => x.State != SessionStates.Completed && x.State != SessionStates.Recovered)
            .Select(x => new StoredSession(x.Session with { EndedAt = x.Ended }, x.State,
                _segments.TryGetValue(x.Session.Id, out var l) ? l.Count : 0))
            .ToList();
        return Task.FromResult(result);
    }

    public override Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _sessions.TryRemove(sessionId, out _);
        _segments.TryRemove(sessionId, out _);
        foreach (var job in _jobs.Where(x => x.Value.SessionId == sessionId).Select(x => x.Key).ToArray())
        {
            _jobs.TryRemove(job, out _);
        }
        return Task.CompletedTask;
    }

    // ----- Translation jobs (Milestone 6) -----

    public override Task<TranscriptSegment?> GetSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        foreach (var list in _segments.Values)
        {
            lock (list)
            {
                var hit = list.FirstOrDefault(x => x.Seg.Id == segmentId);
                if (hit.Seg is not null)
                {
                    return Task.FromResult<TranscriptSegment?>(hit.Seg);
                }
            }
        }

        return Task.FromResult<TranscriptSegment?>(null);
    }

    public override Task CreateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        bool activeExists = _jobs.Values.Any(j => j.SegmentId == job.SegmentId && IsActive(j.State));
        if (activeExists)
        {
            throw new StorageException("constraint", "已存在该字幕的有效翻译任务。");
        }

        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public override Task UpdateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public override Task<TranslationJob?> GetActiveJobForSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
        => Task.FromResult(_jobs.Values.FirstOrDefault(j => j.SegmentId == segmentId && IsActive(j.State)));

    public override Task<IReadOnlyList<TranslationJob>> GetResumableJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslationJob> result = _jobs.Values
            .Where(j => j.State is TranslationJobState.Pending or TranslationJobState.RetryScheduled)
            .OrderBy(j => j.CreatedAt).ToList();
        return Task.FromResult(result);
    }

    public override Task<int> RecoverInProgressJobsAsync(CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (var j in _jobs.Values.Where(j => j.State == TranslationJobState.InProgress).ToList())
        {
            _jobs[j.Id] = j with { State = TranslationJobState.Pending, UpdatedAt = DateTimeOffset.UtcNow };
            count++;
        }

        return Task.FromResult(count);
    }

    public override Task<IReadOnlyList<TranslationJob>> GetJobsForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslationJob> result = _jobs.Values
            .Where(j => j.SessionId == sessionId).OrderBy(j => j.CreatedAt).ToList();
        return Task.FromResult(result);
    }

    public override Task SetSegmentTranslationAsync(Guid segmentId, string? translation, TranscriptStatus status, CancellationToken cancellationToken)
    {
        foreach (var list in _segments.Values)
        {
            lock (list)
            {
                int idx = list.FindIndex(x => x.Seg.Id == segmentId);
                if (idx >= 0)
                {
                    list[idx] = (list[idx].Seg with { Translation = translation, Status = status }, list[idx].Seq);
                    return Task.CompletedTask;
                }
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsActive(TranslationJobState state)
        => state is TranslationJobState.Pending or TranslationJobState.InProgress or TranslationJobState.RetryScheduled;
}
