namespace KikuCaption.Summarization;

/// <summary>The validated time span of a session's final captions (UI-R5C §duration).</summary>
public sealed record MeetingTimeRange(bool HasValid, TimeSpan Start, TimeSpan End, TimeSpan Duration);

/// <summary>
/// Computes the session time range from final captions — the ONE calculation shared by the dialog
/// display and the Markdown metadata (so they never disagree). It uses min(Start)/max(End) over
/// segments with non-reversed timestamps (so gaps and overlaps are handled correctly), clamps the
/// duration to ≥ 0, and reports HasValid=false when there is no usable span (no segments, all
/// zero-length, or only reversed timestamps) so the caller can show a localized "unknown".
/// </summary>
public static class MeetingSummaryTimeRange
{
    public static MeetingTimeRange Compute(IEnumerable<MeetingSummarySegment> segments)
    {
        var valid = segments.Where(s => s.End >= s.Start).ToList(); // drop reversed timestamps
        if (valid.Count == 0)
        {
            return new MeetingTimeRange(false, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        var start = valid.Min(s => s.Start);
        var end = valid.Max(s => s.End);
        var duration = end - start;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        // A positive span means we have real timestamps; an all-zero set is "unknown", never a wrong 00:00.
        return new MeetingTimeRange(end > start, start, end, duration);
    }
}
