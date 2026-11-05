namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// The adaptive re-transcription interval control law (R-perf), kept pure so it is deterministically
/// unit-testable. It steers the next interval from the last cycle's decode time: target ~2x the decode
/// time (so decode stays ~50% of the cycle, leaving head-room to stay ahead of real time), EMA-smoothed
/// to avoid oscillation, then clamped to <paramref name="minMs"/>..<paramref name="maxMs"/>. Fast
/// hardware → small decode → interval falls toward the floor (lower latency); slow hardware → large
/// decode → interval rises toward the ceiling (the fixed value). So it is never slower than fixed
/// (maxMs) and never faster than the floor (minMs).
/// </summary>
public static class AdaptiveIntervalController
{
    public static int Next(long lastDecodeMs, int currentMs, int minMs, int maxMs)
    {
        if (lastDecodeMs <= 0)
        {
            return Math.Clamp(currentMs, minMs, maxMs); // no decode measured yet → hold
        }

        double target = lastDecodeMs * 2.0;                   // decode ~50% of the interval
        double smoothed = 0.5 * target + 0.5 * currentMs;     // EMA damping
        return (int)Math.Clamp(smoothed, minMs, maxMs);
    }
}
