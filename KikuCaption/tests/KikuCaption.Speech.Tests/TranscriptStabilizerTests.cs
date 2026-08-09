using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class TranscriptStabilizerTests
{
    private static TranscriptStabilizer Create(int recent = 2) =>
        new(new ProgressiveCaptionOptions { RecentCandidates = recent }, Guid.NewGuid(), "ja");

    private static TranscriptUpdate Candidate(string text) => new()
    {
        SessionId = Guid.Empty,
        Kind = TranscriptUpdateKind.FinalCandidate,
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.FromSeconds(1),
        Text = text,
        Sequence = 0
    };

    [Fact] // 1. candidates grow → committed prefix grows
    public void GrowingCandidates_CommitGrows()
    {
        var s = Create();
        s.Process(Candidate("你好"));
        var r2 = s.Process(Candidate("你好世界"));
        var r3 = s.Process(Candidate("你好世界啊"));
        Assert.Equal("你好", r2.StableText);
        Assert.Equal("你好世界", r3.StableText);
    }

    [Fact] // 2. Japanese without spaces
    public void Japanese_NoSpaces_StabilizesByCharacter()
    {
        var s = Create();
        s.Process(Candidate("今"));
        s.Process(Candidate("今日"));
        var r = s.Process(Candidate("今日は"));
        Assert.Equal("今日", r.StableText);
    }

    [Fact] // 3. Chinese without spaces
    public void Chinese_NoSpaces_StabilizesByCharacter()
    {
        var s = Create();
        s.Process(Candidate("我"));
        s.Process(Candidate("我们"));
        var r = s.Process(Candidate("我们好"));
        Assert.Equal("我们", r.StableText);
    }

    [Fact] // 4. candidate backtracks → committed never retracts
    public void Backtracking_KeepsCommitted()
    {
        var s = Create();
        s.Process(Candidate("你好世界"));
        var committed = s.Process(Candidate("你好世界啊")).StableText;
        var r = s.Process(Candidate("你"));
        Assert.Equal("你好世界", committed);
        Assert.Equal("你好世界", r.StableText);
        Assert.Equal("你好世界", r.PartialText); // display never shows less than committed
    }

    [Fact] // 5. mid rewrite of the tail, committed locked
    public void MidRewrite_TailChanges_CommittedLocked()
    {
        var s = Create();
        s.Process(Candidate("你好世界"));
        s.Process(Candidate("你好世界们"));
        var r = s.Process(Candidate("你好世界朋友"));
        Assert.Equal("你好世界", r.StableText);
        Assert.EndsWith("朋友", r.PartialText);
    }

    [Fact] // 6. punctuation difference
    public void PunctuationDifference_StableIgnoresTrailingPunct()
    {
        var s = Create();
        s.Process(Candidate("你好"));
        var r = s.Process(Candidate("你好。"));
        Assert.Equal("你好", r.StableText);
    }

    [Fact] // 7. whitespace difference
    public void WhitespaceDifference_Ignored()
    {
        var s = Create();
        s.Process(Candidate("你 好"));
        var r = s.Process(Candidate("你好"));
        Assert.Equal("你好", r.StableText);
    }

    [Fact] // 8. empty candidate does not disturb state
    public void EmptyCandidate_NoChange()
    {
        var s = Create();
        s.Process(Candidate("你好"));
        s.Process(Candidate("你好世界"));
        var r = s.Process(Candidate(""));
        Assert.Equal("你好", r.StableText);
        Assert.Equal("你好世界", r.PartialText);
    }

    [Fact] // 9. overlap repetition does not double the committed text
    public void OverlapRepeat_NoDoubling()
    {
        var s = Create();
        s.Process(Candidate("你好"));
        var r = s.Process(Candidate("你好"));
        Assert.Equal("你好", r.StableText);
    }

    [Fact] // 10. after flush, already-final text is not repeated
    public void AfterFlush_DoesNotRepeatFinal()
    {
        var s = Create();
        s.Process(Candidate("你好世界"));
        s.Process(Candidate("你好世界"));
        var finals = s.Flush(TimeSpan.FromSeconds(2));
        Assert.Single(finals);
        Assert.Equal("你好世界", finals[0].Text);

        s.Process(Candidate("明天"));
        var r = s.Process(Candidate("明天见"));
        Assert.Equal("明天", r.StableText);
        Assert.DoesNotContain("你好", r.PartialText);
    }

    [Fact] // 11. repeated identical candidates stabilize fully
    public void RepeatedIdentical_StabilizesFully()
    {
        var s = Create();
        s.Process(Candidate("你好世界"));
        s.Process(Candidate("你好世界"));
        var r = s.Process(Candidate("你好世界"));
        Assert.Equal("你好世界", r.StableText);
    }

    [Fact] // 12. no common prefix
    public void NoCommonPrefix_EmptyStable()
    {
        var s = Create();
        s.Process(Candidate("你好"));
        var r = s.Process(Candidate("再见"));
        Assert.Equal("", r.StableText);
    }

    [Fact] // extra: RecentCandidates=3 is more conservative
    public void ThreeCandidateAgreement_Works()
    {
        var s = Create(recent: 3);
        s.Process(Candidate("你好世界"));
        s.Process(Candidate("你好世界"));
        var r = s.Process(Candidate("你好世界"));
        Assert.Equal("你好世界", r.StableText);
    }
}
