namespace KikuCaption.Core.Enums;

/// <summary>
/// Kind of a streaming recognition update emitted by <see cref="Interfaces.ISpeechRecognizer"/>.
/// Maps to the worker protocol's <c>partial</c> / <c>final_candidate</c> messages (PROJECT.md 8.3).
/// </summary>
public enum TranscriptUpdateKind
{
    /// <summary>Interim, still-changing text.</summary>
    Partial,

    /// <summary>A candidate final segment. Confirmation/stabilization happens in Milestone 3.</summary>
    FinalCandidate
}
