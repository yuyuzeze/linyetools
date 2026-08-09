using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Models;

/// <summary>
/// A single streaming recognition update (partial or final-candidate) with timestamps,
/// produced by <see cref="Interfaces.ISpeechRecognizer.RecognizeAsync"/> (PROJECT.md 8.3).
/// </summary>
public sealed record TranscriptUpdate
{
    public required Guid SessionId { get; init; }
    public required TranscriptUpdateKind Kind { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }
    public required string Text { get; init; }
    public double? Confidence { get; init; }

    /// <summary>Correlation/sequence number from the worker protocol.</summary>
    public required long Sequence { get; init; }
}
