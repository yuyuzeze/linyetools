using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class FinalizerTests
{
    private static Finalizer Create() => new(new ProgressiveCaptionOptions());

    private static FinalizerSignals Base(bool pending = true) => new(
        HasPendingText: pending,
        EndsWithSentencePunctuation: false,
        StableUnchangedCount: 0,
        SilenceMs: 0,
        UtteranceSeconds: 3,
        WaitSeconds: 3,
        FlushRequested: false);

    [Fact] // 1. sustained silence finalizes (default SilenceFinalMs = 1000)
    public void Silence_Finalizes()
    {
        var r = Create().Evaluate(Base() with { SilenceMs = 1000 });
        Assert.Equal(FinalizeReason.Silence, r);
    }

    [Fact] // 2. punctuation + stability finalizes (default StableRepeatCount = 3)
    public void PunctuationAndStable_Finalizes()
    {
        var r = Create().Evaluate(Base() with { EndsWithSentencePunctuation = true, StableUnchangedCount = 3 });
        Assert.Equal(FinalizeReason.PunctuationStable, r);
    }

    [Fact] // 3. a single brief pause does not finalize
    public void BriefSilence_DoesNotFinalize()
    {
        var r = Create().Evaluate(Base() with { SilenceMs = 300 });
        Assert.Equal(FinalizeReason.None, r);
    }

    [Fact] // 4. max sentence length forces a final
    public void MaxSentenceLength_Finalizes()
    {
        var r = Create().Evaluate(Base() with { UtteranceSeconds = 12 });
        Assert.Equal(FinalizeReason.MaxSentenceLength, r);
    }

    [Fact] // 5. max wait forces a final
    public void MaxWait_Finalizes()
    {
        var r = Create().Evaluate(Base() with { UtteranceSeconds = 5, WaitSeconds = 20 });
        Assert.Equal(FinalizeReason.MaxWait, r);
    }

    [Fact] // 6. flush finalizes pending
    public void Flush_FinalizesPending()
    {
        var r = Create().Evaluate(Base() with { FlushRequested = true });
        Assert.Equal(FinalizeReason.FlushRequested, r);
    }

    [Fact] // 7. empty pending never finalizes (even on flush)
    public void EmptyPending_NeverFinalizes()
    {
        Assert.Equal(FinalizeReason.None, Create().Evaluate(Base(pending: false) with { SilenceMs = 900 }));
        Assert.Equal(FinalizeReason.None, Create().Evaluate(Base(pending: false) with { FlushRequested = true }));
    }

    [Fact] // extra: punctuation without stability does not finalize
    public void PunctuationWithoutStability_DoesNotFinalize()
    {
        var r = Create().Evaluate(Base() with { EndsWithSentencePunctuation = true, StableUnchangedCount = 0 });
        Assert.Equal(FinalizeReason.None, r);
    }
}
