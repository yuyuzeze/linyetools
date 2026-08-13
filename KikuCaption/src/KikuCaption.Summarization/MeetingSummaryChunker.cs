namespace KikuCaption.Summarization;

/// <summary>One Map chunk: contiguous, order-preserving final segments within the budget.</summary>
public sealed record MeetingSummaryChunk(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    IReadOnlyList<MeetingSummarySegment> Segments,
    bool OversizedSingleSegment)
{
    public string Text => string.Join("\n", Segments.Select(s => s.Text));
    public int CharCount => Segments.Sum(s => s.Text.Length);
}

/// <summary>Splits a session's final captions into Map chunks (order-preserving, budget-bounded).</summary>
public interface IMeetingSummaryChunker
{
    IReadOnlyList<MeetingSummaryChunk> Chunk(IReadOnlyList<MeetingSummarySegment> segments, int budgetChars);
}

/// <summary>
/// Default chunker (UI-R5C §8). Combines whole <see cref="MeetingSummarySegment"/>s in
/// <see cref="MeetingSummarySegment.Sequence"/> order until the next would exceed the budget; it never
/// splits a segment. A single segment longer than the budget becomes its own chunk (never silently
/// dropped) and is flagged so the caller can log a warning. Pure and deterministic.
/// </summary>
public sealed class MeetingSummaryChunker : IMeetingSummaryChunker
{
    public IReadOnlyList<MeetingSummaryChunk> Chunk(IReadOnlyList<MeetingSummarySegment> segments, int budgetChars)
    {
        var budget = Math.Clamp(budgetChars, 500, 20000); // defensive: never 0/negative/unbounded
        var chunks = new List<MeetingSummaryChunk>();
        if (segments.Count == 0)
        {
            return chunks;
        }

        // Preserve time order regardless of input ordering.
        var ordered = segments.OrderBy(s => s.Sequence).ToList();

        var current = new List<MeetingSummarySegment>();
        int currentChars = 0;

        foreach (var seg in ordered)
        {
            int len = seg.Text.Length;

            // An over-budget single segment: flush what we have, then emit it alone (flagged).
            if (len > budget)
            {
                if (current.Count > 0)
                {
                    chunks.Add(Build(chunks.Count, current, oversized: false));
                    current = new List<MeetingSummarySegment>();
                    currentChars = 0;
                }

                chunks.Add(Build(chunks.Count, new List<MeetingSummarySegment> { seg }, oversized: true));
                continue;
            }

            // Adding this segment would exceed the budget → start a new chunk first.
            if (current.Count > 0 && currentChars + len > budget)
            {
                chunks.Add(Build(chunks.Count, current, oversized: false));
                current = new List<MeetingSummarySegment>();
                currentChars = 0;
            }

            current.Add(seg);
            currentChars += len;
        }

        if (current.Count > 0)
        {
            chunks.Add(Build(chunks.Count, current, oversized: false));
        }

        return chunks;
    }

    private static MeetingSummaryChunk Build(int index, List<MeetingSummarySegment> segs, bool oversized)
        => new(index, segs[0].Start, segs[^1].End, segs, oversized);
}
