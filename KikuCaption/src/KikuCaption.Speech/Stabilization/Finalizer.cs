namespace KikuCaption.Speech.Stabilization;

public enum FinalizeReason
{
    None,
    Silence,
    PunctuationStable,
    MaxSentenceLength,
    MaxWait,
    FlushRequested
}

/// <summary>Snapshot of the signals a finalize decision is made from (pure, testable).</summary>
public readonly record struct FinalizerSignals(
    bool HasPendingText,
    bool EndsWithSentencePunctuation,
    int StableUnchangedCount,
    int SilenceMs,
    double UtteranceSeconds,
    double WaitSeconds,
    bool FlushRequested);

/// <summary>
/// Decides when the current utterance becomes final (PROJECT.md 9). Combines several signals so
/// no single one over-fragments normal speech; long sentences are protected by a max length /
/// max wait; a brief pause does not finalize. Pure function of its input — no timers/IO.
/// </summary>
public sealed class Finalizer
{
    private readonly ProgressiveCaptionOptions _options;

    public Finalizer(ProgressiveCaptionOptions options) => _options = options;

    public FinalizeReason Evaluate(in FinalizerSignals s)
    {
        // Never finalize empty pending text.
        if (!s.HasPendingText)
        {
            return FinalizeReason.None;
        }

        // Explicit user stop / flush wins.
        if (s.FlushRequested)
        {
            return FinalizeReason.FlushRequested;
        }

        // Long-sentence protections.
        if (s.UtteranceSeconds >= _options.MaxSentenceSeconds)
        {
            return FinalizeReason.MaxSentenceLength;
        }

        if (s.WaitSeconds >= _options.MaxWaitSeconds)
        {
            return FinalizeReason.MaxWait;
        }

        // Sustained silence (a single brief pause below the threshold does not qualify).
        if (s.SilenceMs >= _options.SilenceFinalMs)
        {
            return FinalizeReason.Silence;
        }

        // Sentence-ending punctuation that has held stable for a few cycles.
        if (s.EndsWithSentencePunctuation && s.StableUnchangedCount >= _options.StableRepeatCount)
        {
            return FinalizeReason.PunctuationStable;
        }

        return FinalizeReason.None;
    }
}
