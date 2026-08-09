using KikuCaption.Core.Models;

namespace KikuCaption.Storage.Sqlite;

/// <summary>Well-known session states persisted in the database.</summary>
public static class SessionStates
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Recovered = "Recovered";
    public const string StoppedDiskFull = "StoppedDiskFull";
}

/// <summary>A session row plus persistence-only fields.</summary>
public sealed record StoredSession(MeetingSession Session, string State, int SegmentCount);

/// <summary>A segment row plus its stable per-session sequence number.</summary>
public sealed record StoredSegment(TranscriptSegment Segment, long SequenceNumber);
