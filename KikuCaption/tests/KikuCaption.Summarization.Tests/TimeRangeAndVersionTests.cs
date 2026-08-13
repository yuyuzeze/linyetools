using System.IO;
using Xunit;

namespace KikuCaption.Summarization.Tests;

/// <summary>UI-R5C fixes: validated duration/time-range and collision-safe versioned file names.</summary>
public class TimeRangeAndVersionTests
{
    private static MeetingSummarySegment Seg(long seq, double startSec, double endSec)
        => new(seq, TimeSpan.FromSeconds(startSec), TimeSpan.FromSeconds(endSec), $"t{seq}");

    // ---- time range (scenarios 11-16) -----------------------------------

    [Fact] // 11: normal contiguous segments → min start, max end
    public void Range_Contiguous()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 0, 5), Seg(2, 5, 10), Seg(3, 10, 20) });
        Assert.True(r.HasValid);
        Assert.Equal(TimeSpan.Zero, r.Start);
        Assert.Equal(TimeSpan.FromSeconds(20), r.End);
        Assert.Equal(TimeSpan.FromSeconds(20), r.Duration);
    }

    [Fact] // 12: a gap in the middle still uses max-min (not a sum)
    public void Range_WithGap()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 0, 5), Seg(2, 100, 110) });
        Assert.Equal(TimeSpan.FromSeconds(110), r.Duration); // 110-0, not 5+10
    }

    [Fact] // 13: overlapping segments are handled by min/max
    public void Range_Overlapping()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 0, 30), Seg(2, 10, 20) });
        Assert.Equal(TimeSpan.FromSeconds(30), r.Duration);
    }

    [Fact] // 14: over an hour
    public void Range_OverOneHour()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 0, 10), Seg(2, 3600, 3725) }); // ~1h2m5s
        Assert.True(r.Duration.TotalHours >= 1);
        Assert.Equal(TimeSpan.FromSeconds(3725), r.Duration);
    }

    [Fact] // 15: no valid timestamps (all zero) → unknown (never a wrong 00:00)
    public void Range_NoTimestamps_Unknown()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 0, 0), Seg(2, 0, 0) });
        Assert.False(r.HasValid);
    }

    [Fact] // reversed timestamps are dropped; if none remain → unknown
    public void Range_Reversed_Unknown()
    {
        var r = MeetingSummaryTimeRange.Compute(new[] { Seg(1, 10, 5) });
        Assert.False(r.HasValid);
    }

    [Fact] // empty → unknown
    public void Range_Empty_Unknown()
        => Assert.False(MeetingSummaryTimeRange.Compute(System.Array.Empty<MeetingSummarySegment>()).HasValid);

    [Fact] // 16: the Markdown time range uses the same computation (range shown when valid, else "unknown")
    public void Markdown_TimeRange_MatchesComputation()
    {
        var exporter = new MarkdownMeetingSummaryExporter();
        var valid = Doc(TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var unknown = Doc(TimeSpan.Zero, TimeSpan.Zero);
        Assert.Contains("00:00 – 05:00", exporter.Render(valid));
        Assert.Contains("未知", exporter.Render(unknown)); // zh default document
    }

    private static MeetingSummaryDocument Doc(TimeSpan start, TimeSpan end) => new()
    {
        SessionId = Guid.NewGuid(), MeetingType = MeetingType.SinglePresenter, OutputLanguage = "zh",
        Model = "m", PromptVersion = MeetingSummaryPrompt.Version, GeneratedAt = DateTimeOffset.Now,
        SessionDate = DateTimeOffset.Now, SegmentCount = 3, Start = start, End = end, Sections = new MeetingSummarySections()
    };

    // ---- versioned file names (scenarios 33/34) --------------------------

    [Fact] // 33: a version name never equals the default file
    public void Versioned_IsNotDefault()
    {
        var e = new MarkdownMeetingSummaryExporter();
        var name = e.VersionedFileName(new DateTimeOffset(2026, 8, 12, 9, 30, 5, TimeSpan.Zero));
        Assert.NotEqual(e.DefaultFileName, name);
        Assert.Equal("meeting-summary-20260812-093005.md", name);
    }

    [Fact] // 34: a same-second collision gets a safe -N suffix, still inside the directory
    public void Versioned_SameSecond_GetsSuffix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_ver", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var e = new MarkdownMeetingSummaryExporter();
            var ts = new DateTimeOffset(2026, 8, 12, 9, 30, 5, TimeSpan.Zero);
            var first = e.VersionedFileName(ts);
            File.WriteAllText(Path.Combine(dir, first), "x"); // occupy the base version name

            var next = e.UniqueVersionedFileName(dir, ts);
            Assert.NotEqual(first, next);
            Assert.Equal("meeting-summary-20260812-093005-2.md", next);
            Assert.Equal(next, Path.GetFileName(MarkdownMeetingSummaryExporter.ResolveSafePath(dir, next))); // inside dir
        }
        finally { Directory.Delete(dir, true); }
    }
}
