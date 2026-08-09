namespace KikuCaption.Core.Models;

/// <summary>Outcome of a recording (PROJECT.md 8.2, 16). <see cref="IsComplete"/> is true only
/// when FFmpeg exited cleanly and the file is a playable MP4 — never claimed for a broken file.</summary>
public sealed record RecordingResult
{
    public required string OutputPath { get; init; }
    public required bool IsComplete { get; init; }
    public required string Encoder { get; init; }
    public int? ExitCode { get; init; }
    public long FileSizeBytes { get; init; }
    public TimeSpan? VideoDuration { get; init; }
    public TimeSpan? AudioDuration { get; init; }

    /// <summary>User-safe status/diagnostic message (no PCM/subtitle content).</summary>
    public string? Message { get; init; }
}
