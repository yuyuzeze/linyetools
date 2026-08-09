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

/// <summary>Observable performance/back-pressure metrics (PROJECT.md 14.2).</summary>
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
}
