namespace KikuCaption.Core.Models;

/// <summary>
/// Metadata for one meeting/recording session (PROJECT.md 8.1, 12).
/// </summary>
public sealed record MeetingSession
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Chosen recognition language, "ja" or "zh".</summary>
    public required string RecognitionLanguage { get; init; }

    /// <summary>Directory that holds this session's outputs (mp4, transcripts, session.json).</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Path to the recorded MP4, when recording is enabled.</summary>
    public string? RecordingPath { get; init; }
}
