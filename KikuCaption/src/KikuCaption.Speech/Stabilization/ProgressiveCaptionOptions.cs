namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// Tunable, range-validated parameters for progressive captioning (PROJECT.md 9). No magic
/// numbers scattered in the algorithm — everything lives here and is validated on startup.
/// </summary>
public sealed class ProgressiveCaptionOptions
{
    /// <summary>How often a partial transcription cycle runs (500–1000 ms).</summary>
    public int PartialIntervalMs { get; init; } = 800;

    /// <summary>Inference audio window target (2–6 s).</summary>
    public double WindowSeconds { get; init; } = 4;

    /// <summary>Overlap between consecutive windows (1–2 s).</summary>
    public double OverlapSeconds { get; init; } = 1.5;

    /// <summary>How many recent candidates must agree for a stable prefix (2–3).</summary>
    public int RecentCandidates { get; init; } = 2;

    /// <summary>Continuous silence that finalizes the current utterance (500–800 ms).</summary>
    public int SilenceFinalMs { get; init; } = 700;

    /// <summary>Maximum utterance audio length before a forced final (&gt;= WindowSeconds).</summary>
    public double MaxSentenceSeconds { get; init; } = 12;

    /// <summary>Maximum wall time to wait before a forced final (&gt;= MaxSentenceSeconds).</summary>
    public double MaxWaitSeconds { get; init; } = 20;

    /// <summary>Cycles the stable prefix must stay unchanged (with punctuation) to finalize (&gt;= 1).</summary>
    public int StableRepeatCount { get; init; } = 2;

    /// <summary>Max caption lines shown in the overlay (2–5).</summary>
    public int MaxLines { get; init; } = 4;

    /// <summary>RMS energy threshold below which audio counts as silence (0..1).</summary>
    public double SilenceRmsThreshold { get; init; } = 0.012;

    public void Validate()
    {
        Range(PartialIntervalMs, 500, 1000, nameof(PartialIntervalMs));
        Range(WindowSeconds, 2, 6, nameof(WindowSeconds));
        Range(OverlapSeconds, 1, 2, nameof(OverlapSeconds));
        Range(RecentCandidates, 2, 3, nameof(RecentCandidates));
        Range(SilenceFinalMs, 500, 800, nameof(SilenceFinalMs));
        Range(MaxLines, 2, 5, nameof(MaxLines));

        if (StableRepeatCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(StableRepeatCount), StableRepeatCount, "必须 >= 1。");
        }

        if (MaxSentenceSeconds < WindowSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSentenceSeconds), MaxSentenceSeconds,
                "MaxSentenceSeconds 必须 >= WindowSeconds。");
        }

        if (MaxWaitSeconds < MaxSentenceSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWaitSeconds), MaxWaitSeconds,
                "MaxWaitSeconds 必须 >= MaxSentenceSeconds。");
        }

        if (SilenceRmsThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SilenceRmsThreshold), SilenceRmsThreshold, "必须在 [0,1]。");
        }
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"必须在 [{min}, {max}]。");
        }
    }
}
