namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// CJK-aware de-duplication at a sliding-window seam (PROJECT.md 9). When a window advances and keeps
/// an audio overlap, the next window can re-transcribe the tail of the text that was just finalized;
/// this strips the longest leading run of <paramref name="next"/> that repeats the trailing run of
/// <paramref name="previous"/>, so a window advance never emits duplicated final text.
/// </summary>
public static class SeamDedup
{
    public static string StripLeadingOverlap(string previous, string next)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(next))
        {
            return next ?? string.Empty;
        }

        var prev = CaptionText.SignificantRunes(previous).ToArray();
        var nxt = CaptionText.SignificantRunes(next).ToArray();
        int maxOverlap = Math.Min(prev.Length, nxt.Length);

        for (int k = maxOverlap; k >= 1; k--)
        {
            bool match = true;
            for (int i = 0; i < k; i++)
            {
                if (prev[prev.Length - k + i] != nxt[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return CaptionText.SkipSignificantPrefix(next, k);
            }
        }

        return next;
    }
}
