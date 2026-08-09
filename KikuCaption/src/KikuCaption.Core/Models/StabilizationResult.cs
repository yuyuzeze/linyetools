namespace KikuCaption.Core.Models;

/// <summary>
/// Result of feeding one recognition candidate to <see cref="Interfaces.ITranscriptStabilizer"/>.
/// <see cref="StableText"/> is the committed prefix agreed across recent candidates (monotonic,
/// never retracted within an utterance); <see cref="PartialText"/> is the full current best text
/// for display (PROJECT.md 9).
/// </summary>
public sealed record StabilizationResult
{
    public required string StableText { get; init; }
    public required string PartialText { get; init; }

    /// <summary>True when the committed stable prefix grew on this update.</summary>
    public required bool StableAdvanced { get; init; }

    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }

    public static StabilizationResult Empty { get; } = new()
    {
        StableText = string.Empty,
        PartialText = string.Empty,
        StableAdvanced = false
    };
}
