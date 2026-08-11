namespace KikuCaption.Core.Models;

/// <summary>
/// Validates and normalizes a recognition hotword glossary (technical terms) before it is sent to
/// the worker. Bounds the number of entries, each term's length, and the total size so an oversized
/// or hostile glossary can never bloat the request or the prompt. The full list is never logged.
/// </summary>
public static class Hotwords
{
    public const int MaxCount = 64;
    public const int MaxTermLength = 40;
    public const int MaxTotalCharacters = 1000;

    /// <summary>
    /// Trims/deduplicates entries and enforces the limits. Empty entries are dropped. Throws
    /// <see cref="ArgumentException"/> if the (cleaned) glossary exceeds a hard limit.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? terms)
    {
        if (terms is null)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        int totalChars = 0;

        foreach (var raw in terms)
        {
            var term = (raw ?? string.Empty).Trim();
            if (term.Length == 0 || !seen.Add(term))
            {
                continue;
            }

            if (term.Length > MaxTermLength)
            {
                throw new ArgumentException($"术语「{Truncate(term)}」长度 {term.Length} 超过上限 {MaxTermLength}。", nameof(terms));
            }

            result.Add(term);
            totalChars += term.Length;

            if (result.Count > MaxCount)
            {
                throw new ArgumentException($"术语表条目数 {result.Count} 超过上限 {MaxCount}。", nameof(terms));
            }

            if (totalChars > MaxTotalCharacters)
            {
                throw new ArgumentException($"术语表总字符数 {totalChars} 超过上限 {MaxTotalCharacters}。", nameof(terms));
            }
        }

        return result;
    }

    /// <summary>Joins normalized terms into the single space-separated string faster-whisper expects.</summary>
    public static string? ToWireString(IReadOnlyList<string>? terms)
        => terms is { Count: > 0 } ? string.Join(' ', terms) : null;

    private static string Truncate(string s) => s.Length <= 12 ? s : s[..12] + "…";
}
