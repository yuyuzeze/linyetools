namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// Briefly holds very short, unpunctuated candidates before finalizing (PROJECT.md 9), so a mid-speech
/// fragment like「まどぐち」is not finalized on its own the instant a tiny pause trips the silence
/// threshold — it gets a chance to merge with continuing speech. A genuine short reply like「はい」is
/// still emitted once the hold elapses, never lost. Hard reasons (flush / max-length / max-wait) are
/// never held. Pure except for the caller-supplied clock, so it is fully unit-testable.
/// </summary>
public sealed class ShortFragmentGate
{
    private readonly ProgressiveCaptionOptions _options;
    private long _holdStartMs = -1;

    public ShortFragmentGate(ProgressiveCaptionOptions options) => _options = options;

    /// <summary>True if currently holding a fragment (nothing has been finalized yet).</summary>
    public bool IsHolding => _holdStartMs >= 0;

    /// <summary>
    /// Decides whether the utterance may finalize now. Returns false to hold this cycle. While held,
    /// the pipeline keeps accumulating audio, so continuing speech naturally merges into one final.
    /// </summary>
    public bool ShouldFinalize(FinalizeReason reason, int significantRunes, bool endsWithPunctuation, long nowMs)
    {
        // Hard reasons always win — never hold a flush or a length/wait cap.
        if (reason is FinalizeReason.FlushRequested or FinalizeReason.MaxSentenceLength or FinalizeReason.MaxWait)
        {
            Reset();
            return true;
        }

        bool isShortFragment = significantRunes > 0
                               && significantRunes <= _options.ShortFragmentMaxRunes
                               && !endsWithPunctuation;

        if (!isShortFragment)
        {
            // Long enough, or ends a sentence → finalize normally (also covers "merged" continuations).
            Reset();
            return true;
        }

        if (_holdStartMs < 0)
        {
            _holdStartMs = nowMs; // start the hold; don't finalize yet
            return false;
        }

        if (nowMs - _holdStartMs >= _options.ShortFragmentHoldMs)
        {
            Reset();
            return true; // hold elapsed with no continuation → emit the short reply on its own
        }

        return false; // keep holding
    }

    public void Reset() => _holdStartMs = -1;
}
