namespace KikuCaption.Speech.Stabilization;

/// <summary>Validated parameters for the safe complete-utterance progressive-caption pipeline.</summary>
public sealed class ProgressiveCaptionOptions
{
    public int PartialIntervalMs { get; init; } = 2000;

    // Adaptive latency (R-perf): when true, the re-transcription interval is adjusted dynamically from
    // the measured decode time so it stays as low as the machine can sustain (lower latency on fast
    // hardware) and backs off toward PartialIntervalMs when decoding is slow (no "falling behind").
    // When false, the interval is fixed at PartialIntervalMs (the original, deterministic behavior).
    // The adaptive interval is always clamped to [AdaptiveMinIntervalMs, PartialIntervalMs], so adaptive
    // is never slower than fixed and never faster than the floor.
    public bool AdaptiveLatency { get; init; }
    public int AdaptiveMinIntervalMs { get; init; } = 600;

    public int RecentCandidates { get; init; } = 2;
    public int SilenceFinalMs { get; init; } = 700;
    public double MaxSentenceSeconds { get; init; } = 12;
    public double MaxWaitSeconds { get; init; } = 20;
    public int StableRepeatCount { get; init; } = 2;
    public int ShortFragmentMaxRunes { get; init; } = 4;
    public int ShortFragmentHoldMs { get; init; } = 500;
    public int MaxLines { get; init; } = 4;
    public double SilenceRmsThreshold { get; init; } = 0.012;

    public void Validate()
    {
        // Upper bound widened to 3000 ms: on CPU the real-time path re-transcribes the whole utterance
        // each cycle, so a larger interval is the main lever for keeping the effective real-time factor
        // below 1 on long, continuous speech (measured). 2000 ms is the recommended CPU default.
        Range(PartialIntervalMs, 500, 3000, nameof(PartialIntervalMs));
        // Adaptive floor: must be a sane minimum and never above the fixed ceiling (PartialIntervalMs).
        Range(AdaptiveMinIntervalMs, 300, 3000, nameof(AdaptiveMinIntervalMs));
        if (AdaptiveMinIntervalMs > PartialIntervalMs)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveMinIntervalMs),
                AdaptiveMinIntervalMs, "Must not exceed PartialIntervalMs (the adaptive ceiling).");
        Range(RecentCandidates, 2, 3, nameof(RecentCandidates));
        Range(SilenceFinalMs, 500, 1500, nameof(SilenceFinalMs));
        Range(MaxLines, 1, 5, nameof(MaxLines));
        Range(ShortFragmentMaxRunes, 0, 10, nameof(ShortFragmentMaxRunes));
        Range(ShortFragmentHoldMs, 0, 2000, nameof(ShortFragmentHoldMs));
        if (StableRepeatCount < 1) throw new ArgumentOutOfRangeException(nameof(StableRepeatCount));
        if (MaxSentenceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(MaxSentenceSeconds));
        if (MaxWaitSeconds < MaxSentenceSeconds) throw new ArgumentOutOfRangeException(nameof(MaxWaitSeconds));
        if (SilenceRmsThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(SilenceRmsThreshold));
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, value, $"Must be in [{min}, {max}].");
    }
}
