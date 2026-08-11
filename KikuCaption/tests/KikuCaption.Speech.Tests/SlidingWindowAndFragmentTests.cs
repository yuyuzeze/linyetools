using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class SlidingWindowBufferTests
{
    private const int Bps = SlidingWindowBuffer.BytesPerSecond;

    private static byte[] Seconds(double s) => new byte[(int)(s * Bps)];

    [Fact] // test 7: WindowSeconds actually caps the audio sent to Whisper
    public void TranscriptionWindow_CapsToWindowSeconds()
    {
        var w = new SlidingWindowBuffer(windowSeconds: 6, overlapSeconds: 2);
        w.Append(Seconds(10), TimeSpan.Zero); // 10 s buffered

        var window = w.TranscriptionWindow(out var start);
        double windowSeconds = window.Length / (double)Bps;

        Assert.True(windowSeconds <= 6.0 + 1e-6, $"window {windowSeconds}s exceeds cap");
        Assert.True(windowSeconds >= 5.99, "window should be ~6 s (the tail)");
        Assert.True(start >= TimeSpan.FromSeconds(3.99), "window start advances to the tail");
    }

    [Fact] // shorter-than-window buffer returns the whole buffer
    public void TranscriptionWindow_ShortBuffer_ReturnsAll()
    {
        var w = new SlidingWindowBuffer(6, 2);
        w.Append(Seconds(3), TimeSpan.Zero);
        var window = w.TranscriptionWindow(out var start);
        Assert.Equal(Seconds(3).Length, window.Length);
        Assert.Equal(TimeSpan.Zero, start);
    }

    [Fact] // test 8: advancing keeps exactly OverlapSeconds and re-anchors the start monotonically
    public void AdvanceKeepingOverlap_RetainsOverlap()
    {
        var w = new SlidingWindowBuffer(6, 2);
        w.Append(Seconds(8), TimeSpan.Zero); // 8 s
        Assert.True(w.ExceedsWindow);

        var endTime = TimeSpan.FromSeconds(8);
        w.AdvanceKeepingOverlap(endTime);

        double kept = w.ByteCount / (double)Bps;
        Assert.Equal(2.0, kept, 2);                       // overlap retained
        Assert.Equal(6.0, w.StartTime.TotalSeconds, 2);   // start = end - overlap (monotonic)
        Assert.False(w.ExceedsWindow);
    }
}

public class SeamDedupTests
{
    [Fact] // test 9: a window seam does not repeat already-final text (CJK, no spaces)
    public void StripLeadingOverlap_RemovesRepeatedPrefix_Japanese()
    {
        // Previous final ended with 「について」; the overlapping next window re-heard it.
        var deduped = SeamDedup.StripLeadingOverlap("今回のリリースについて", "について確認します。");
        Assert.Equal("確認します。", deduped);
    }

    [Fact]
    public void StripLeadingOverlap_NoOverlap_ReturnsNextUnchanged()
    {
        Assert.Equal("全く別の文です。", SeamDedup.StripLeadingOverlap("前の文。", "全く別の文です。"));
    }

    [Fact]
    public void StripLeadingOverlap_FullDuplicate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SeamDedup.StripLeadingOverlap("はい確認します", "確認します"));
    }
}

public class ShortFragmentGateTests
{
    private static ProgressiveCaptionOptions Opt() => new() { ShortFragmentMaxRunes = 4, ShortFragmentHoldMs = 500 };

    [Fact] // test 12: a short, unpunctuated fragment is held (not finalized immediately)
    public void ShortUnpunctuated_IsHeld()
    {
        var gate = new ShortFragmentGate(Opt());
        // 「まどぐち」= 4 significant runes, no punctuation, would finalize on Silence.
        bool ok = gate.ShouldFinalize(FinalizeReason.Silence, significantRunes: 4, endsWithPunctuation: false, nowMs: 1000);
        Assert.False(ok);
        Assert.True(gate.IsHolding);
    }

    [Fact] // test 13: if the fragment grows (speech continued), it merges → finalize the longer text
    public void ShortFragment_ThatGrows_Merges()
    {
        var gate = new ShortFragmentGate(Opt());
        Assert.False(gate.ShouldFinalize(FinalizeReason.Silence, 4, false, 1000)); // held
        // Next cycle the candidate is now long (「まどぐちについて確認します」) → no longer a fragment.
        bool ok = gate.ShouldFinalize(FinalizeReason.Silence, significantRunes: 12, endsWithPunctuation: false, nowMs: 1200);
        Assert.True(ok); // finalize the merged, longer utterance
        Assert.False(gate.IsHolding);
    }

    [Fact] // test 14: a genuine short reply「はい」is emitted on its own after the hold, never lost
    public void ShortReply_AfterHold_IsFinalized()
    {
        var gate = new ShortFragmentGate(Opt());
        Assert.False(gate.ShouldFinalize(FinalizeReason.Silence, 2, false, 1000)); // 「はい」held
        Assert.False(gate.ShouldFinalize(FinalizeReason.Silence, 2, false, 1300)); // still within hold
        bool ok = gate.ShouldFinalize(FinalizeReason.Silence, 2, false, 1550);     // hold (500ms) elapsed
        Assert.True(ok);
    }

    [Fact] // hard reasons (flush / max) are never held
    public void HardReasons_NeverHeld()
    {
        var gate = new ShortFragmentGate(Opt());
        Assert.True(gate.ShouldFinalize(FinalizeReason.FlushRequested, 2, false, 1000));
        Assert.True(gate.ShouldFinalize(FinalizeReason.MaxWait, 2, false, 1000));
    }

    [Fact] // punctuated or long candidates finalize normally (not held)
    public void PunctuatedOrLong_NotHeld()
    {
        var gate = new ShortFragmentGate(Opt());
        Assert.True(gate.ShouldFinalize(FinalizeReason.Silence, 2, endsWithPunctuation: true, nowMs: 1000));
        Assert.True(gate.ShouldFinalize(FinalizeReason.Silence, 10, endsWithPunctuation: false, nowMs: 1000));
    }
}

public class CaptionTextCjkTests
{
    [Fact] // test 11: spaceless Japanese stable-prefix comparison (per Unicode rune)
    public void CommonSignificantPrefix_Japanese_NoSpaces()
    {
        var candidates = new[] { "今日は会議で", "今日は会議です" };
        int common = CaptionText.CommonSignificantPrefixCount(candidates);
        Assert.Equal(6, common); // 今 日 は 会 議 で
        Assert.Equal("今日は会議で", CaptionText.TakeSignificantPrefix("今日は会議です", common));
    }

    [Fact]
    public void SkipSignificantPrefix_Japanese()
    {
        Assert.Equal("確認します。", CaptionText.SkipSignificantPrefix("について確認します。", 4)); // drop に・つ・い・て
    }
}
