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

    // UI-R4A: immutable translation direction chosen when this meeting started (null for legacy
    // sessions). Recorded in session.json and used to keep a whole session's translation.srt in one
    // target language, even across crash recovery.
    public bool? TranslationEnabled { get; init; }
    public string? TranslationSource { get; init; }
    public string? TranslationTarget { get; init; }

    /// <summary>The translation model chosen when this meeting started (null for legacy sessions).</summary>
    public string? TranslationModel { get; init; }
}
