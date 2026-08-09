using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Models;

/// <summary>
/// A single unit of recognized speech and its optional translation (PROJECT.md 8.1, 12).
/// </summary>
public sealed record TranscriptSegment
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }

    /// <summary>Recognition language code, e.g. "ja" or "zh".</summary>
    public required string Language { get; init; }

    /// <summary>Recognized original-language text.</summary>
    public required string Text { get; init; }

    /// <summary>Translated text (only for Japanese final segments once translated).</summary>
    public string? Translation { get; init; }

    public required TranscriptStatus Status { get; init; }
    public double? Confidence { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
