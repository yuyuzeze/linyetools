using System.Text;

namespace KikuCaption.Speech.Stabilization;

/// <summary>CJK-aware text helpers shared by the stabilizer and finalizer.</summary>
public static class CaptionText
{
    // Sentence-ending punctuation for CJK and Latin.
    private static readonly HashSet<char> SentenceEnders = new(
        new[] { '。', '．', '！', '？', '…', '.', '!', '?' });

    /// <summary>Runes that are not whitespace (the "significant" units for prefix matching).</summary>
    public static IEnumerable<Rune> SignificantRunes(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune))
            {
                yield return rune;
            }
        }
    }

    public static int SignificantCount(string text) => SignificantRunes(text).Count();

    /// <summary>True if <paramref name="text"/>'s significant runes begin with <paramref name="prefix"/>'s.</summary>
    public static bool SignificantStartsWith(string text, string prefix)
    {
        using var textRunes = SignificantRunes(text).GetEnumerator();
        foreach (var prefixRune in SignificantRunes(prefix))
        {
            if (!textRunes.MoveNext() || textRunes.Current != prefixRune)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Count of leading significant runes common to every candidate (whitespace-insensitive).</summary>
    public static int CommonSignificantPrefixCount(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var sequences = candidates.Select(c => SignificantRunes(c).ToArray()).ToArray();
        int min = sequences.Min(s => s.Length);

        int k = 0;
        for (; k < min; k++)
        {
            var first = sequences[0][k];
            for (int i = 1; i < sequences.Length; i++)
            {
                if (sequences[i][k] != first)
                {
                    return k;
                }
            }
        }

        return k;
    }

    /// <summary>Returns the prefix of <paramref name="text"/> covering its first <paramref name="k"/> significant runes.</summary>
    public static string TakeSignificantPrefix(string text, int k)
    {
        if (k <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        int count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            builder.Append(rune.ToString());
            if (!Rune.IsWhiteSpace(rune))
            {
                count++;
                if (count == k)
                {
                    break;
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Returns <paramref name="text"/> with its first <paramref name="k"/> significant runes removed
    /// (whitespace between/after is preserved). Used for CJK-aware seam de-duplication.</summary>
    public static string SkipSignificantPrefix(string text, int k)
    {
        if (k <= 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        int skipped = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (skipped < k)
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    skipped++;
                }

                continue; // drop this rune (significant or the whitespace around the skipped prefix)
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString().TrimStart();
    }

    public static bool EndsWithSentencePunctuation(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return SentenceEnders.Contains(c);
        }

        return false;
    }
}
