using KikuCaption.Speech.Stabilization;

namespace KikuCaption.Speech.Streaming;

public enum CaptionPipelineState
{
    Idle,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

public sealed class CaptionPartialEventArgs : EventArgs
{
    public required string PartialText { get; init; }
    public required string StableText { get; init; }
}

public sealed class CaptionFinalEventArgs : EventArgs
{
    public required string Text { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }
    public required FinalizeReason Reason { get; init; }
}

public sealed class CaptionFaultedEventArgs : EventArgs
{
    public required string Message { get; init; }
}

/// <summary>
/// Observable performance/back-pressure metrics (PROJECT.md 14.2), plus audio-accounting diagnostics
/// (data-loss Hotfix) so missing content can be objectively detected without ever logging caption
/// text, prompts, hotwords, or keys — every field here is a number.
/// </summary>
public sealed class CaptionMetrics
{
    public int PartialCount { get; init; }
    public int FinalCount { get; init; }
    public double Rtf { get; init; }
    public long LastInferenceMs { get; init; }
    public long PartialLatencyMs { get; init; }
    public long FinalLatencyMs { get; init; }
    public int QueueDepthMs { get; init; }
    public long SkippedCycles { get; init; }

    /// <summary>Total PCM seconds ingested this session (regardless of loudness/silence).</summary>
    public double AudioReceivedSeconds { get; init; }

    /// <summary>
    /// Total seconds that have been included in at least one Whisper transcription snapshot
    /// (previously-finalized utterances' full length + the current utterance's latest snapshot).
    /// </summary>
    public double AudioIncludedInSnapshotsSeconds { get; init; }

    /// <summary>Total seconds that have produced a final and been removed from the buffer.</summary>
    public double AudioFinalizedSeconds { get; init; }

    /// <summary>
    /// Seconds discarded without ever being finalized OR without ever being sent to Whisper at all
    /// (bounded to the documented sub-100ms end-of-session tail in the safe pipeline).
    /// </summary>
    public double AudioDiscardedSeconds { get; init; }

    /// <summary>
    /// Seconds discarded that were NOT part of the snapshot that produced the just-emitted final —
    /// i.e. genuinely lost, un-final audio. Must always be 0 on the safe (default) path.
    /// </summary>
    public double AudioDiscardedUncommittedSeconds { get; init; }

    /// <summary>Seconds currently buffered, accumulated since the last final boundary.</summary>
    public double PendingAudioSeconds { get; init; }

    /// <summary>Cycles where the recognizer returned an empty/whitespace-only candidate.</summary>
    public long EmptyCandidateCount { get; init; }
}
