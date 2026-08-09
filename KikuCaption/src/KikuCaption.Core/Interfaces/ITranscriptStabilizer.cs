using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Turns a sequence of (growing / rewriting) recognition candidates into a stable, progressively
/// committed transcript (PROJECT.md 8.3, 9). Implementations must be CJK-aware (work on
/// space-free Japanese/Chinese text) and live in <c>KikuCaption.Speech</c> — never in a
/// ViewModel or window code-behind.
/// </summary>
public interface ITranscriptStabilizer
{
    /// <summary>Feeds one candidate (the recognizer's transcription of the current utterance).</summary>
    StabilizationResult Process(TranscriptUpdate update);

    /// <summary>
    /// Emits any pending stabilized text as final segments (e.g. when the user stops), then resets.
    /// </summary>
    IReadOnlyList<TranscriptSegment> Flush(TimeSpan endTime);
}
