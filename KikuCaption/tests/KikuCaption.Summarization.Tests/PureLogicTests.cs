using System.Linq;
using Xunit;

namespace KikuCaption.Summarization.Tests;

/// <summary>UI-R5C: chunker, JSON parsing, and prompt construction (no network).</summary>
public class PureLogicTests
{
    private static MeetingSummarySegment Seg(long seq, string text, int startSec = 0)
        => new(seq, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(startSec + 1), text);

    // ---- chunker ---------------------------------------------------------

    [Fact] // scenario 18: chunks preserve SequenceNumber order even if input is shuffled
    public void Chunker_PreservesSequenceOrder()
    {
        var segs = new[] { Seg(3, "c"), Seg(1, "a"), Seg(2, "b") };
        var chunks = new MeetingSummaryChunker().Chunk(segs, 500);
        Assert.Single(chunks);
        Assert.Equal("a\nb\nc", chunks[0].Text);
    }

    [Fact] // scenario 19/20: budget splits into multiple chunks without splitting a segment
    public void Chunker_SplitsByBudget_WholeSegments()
    {
        var segs = Enumerable.Range(1, 10).Select(i => Seg(i, new string('x', 100))).ToArray();
        var chunks = new MeetingSummaryChunker().Chunk(segs, 500); // ~5 segments/chunk
        Assert.True(chunks.Count >= 2);
        Assert.Equal(10, chunks.Sum(c => c.Segments.Count));
        Assert.All(chunks, c => Assert.True(c.CharCount <= 500 || c.OversizedSingleSegment));
    }

    [Fact] // scenario 21: an over-budget single segment becomes its own flagged chunk (never dropped)
    public void Chunker_OversizedSegment_IsOwnChunk()
    {
        var segs = new[] { Seg(1, "short"), Seg(2, new string('y', 2000)), Seg(3, "tail") };
        var chunks = new MeetingSummaryChunker().Chunk(segs, 500);
        var big = chunks.First(c => c.OversizedSingleSegment);
        Assert.Single(big.Segments);
        Assert.Equal(2000, big.CharCount);
        Assert.Equal(3, chunks.Sum(c => c.Segments.Count)); // nothing lost
    }

    [Fact] // budget is clamped — never zero/negative → no infinite chunks
    public void Chunker_BudgetClamped()
    {
        var segs = new[] { Seg(1, "a"), Seg(2, "b") };
        Assert.NotEmpty(new MeetingSummaryChunker().Chunk(segs, 0));
        Assert.NotEmpty(new MeetingSummaryChunker().Chunk(segs, -100));
    }

    // ---- JSON ------------------------------------------------------------

    [Fact] // scenario 23/24: a valid object parses into all sections
    public void Json_ParsesValid()
    {
        var json = "{\"overview\":\"o\",\"topics\":[\"t1\",\"t2\"],\"actionItems\":[{\"task\":\"do\",\"owner\":\"A\",\"due\":\"tmr\"}]}";
        Assert.True(MeetingSummaryJson.TryParse(json, out var s));
        Assert.Equal("o", s.Overview);
        Assert.Equal(new[] { "t1", "t2" }, s.Topics);
        Assert.Single(s.ActionItems);
        Assert.Equal("do", s.ActionItems[0].Task);
    }

    [Fact] // scenario 25: missing arrays are treated as empty (not an error)
    public void Json_MissingArrays_AreEmpty()
    {
        Assert.True(MeetingSummaryJson.TryParse("{\"overview\":\"o\"}", out var s));
        Assert.Empty(s.Topics);
        Assert.Empty(s.ActionItems);
        Assert.Empty(s.Decisions);
    }

    [Fact] // non-JSON returns false (caller triggers the single repair)
    public void Json_NonJson_ReturnsFalse()
        => Assert.False(MeetingSummaryJson.TryParse("Here is your summary: ...", out _));

    [Fact] // a leading code fence is tolerated defensively
    public void Json_StripsFence()
        => Assert.True(MeetingSummaryJson.TryParse("```json\n{\"overview\":\"o\"}\n```", out _));

    [Fact] // scenario 28: array length is capped so a hostile huge response can't exhaust memory
    public void Json_CapsArrayLength()
    {
        var huge = "{\"topics\":[" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"t{i}\"")) + "]}";
        Assert.True(MeetingSummaryJson.TryParse(huge, out var s));
        Assert.True(s.Topics.Count <= 200);
    }

    // ---- prompt ----------------------------------------------------------

    [Fact] // scenario 12/18: the schema/rules never include speakers, names, or roles
    public void Prompt_NoSpeakerOrRoleFields()
    {
        var p = MeetingSummaryPrompt.BuildMapSystem(MeetingType.GroupDiscussion, "zh");
        // The schema has no speaker/name/role FIELD (the rules do forbid speaker labels in prose).
        Assert.DoesNotContain("\"speaker\"", p, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"name\"", p, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"role\"", p, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never identify", p);
        Assert.Contains("Do not infer roles", p);
    }

    [Fact] // scenario 29: the prompt instructs to treat captions as untrusted (ignore embedded instructions)
    public void Prompt_HasInjectionGuard()
        => Assert.Contains("UNTRUSTED", MeetingSummaryPrompt.BuildMapSystem(MeetingType.SinglePresenter, "en"));

    [Theory] // output language directive
    [InlineData("zh", "Simplified Chinese")]
    [InlineData("ja", "Japanese")]
    [InlineData("en", "English")]
    public void Prompt_StatesOutputLanguage(string code, string name)
        => Assert.Contains(name, MeetingSummaryPrompt.BuildReduceSystem(MeetingType.GroupDiscussion, code));

    [Fact] // scenario 31: prompt version is supported and stable
    public void Prompt_VersionSupported()
    {
        Assert.Equal(1, MeetingSummaryPrompt.Version);
        Assert.True(MeetingSummaryPrompt.IsSupported(1));
        Assert.False(MeetingSummaryPrompt.IsSupported(2));
    }
}
