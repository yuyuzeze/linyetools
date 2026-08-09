using System.Text.RegularExpressions;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>A potential secret found in scanned content.</summary>
public sealed record ScanFinding(string File, int Line, string Pattern);

/// <summary>
/// Automated sensitive-information scanner (Milestone 7 §6). Detects likely plaintext secret
/// <b>values</b> (a bearer token, an api-key assignment, an OpenAI-style key) — not the bare header
/// names <c>Authorization</c>/<c>Bearer</c>/<c>api-key</c> that legitimately appear in source/config.
/// Used by tests and release checks over source, appsettings, logs, SQLite dumps and session.json.
/// </summary>
public static class SensitiveInfoScanner
{
    private static readonly (string Name, Regex Rx)[] Patterns =
    {
        ("bearer-token", new Regex(@"(?i)authorization\s*[:=]\s*bearer\s+[A-Za-z0-9._\-]{12,}", RegexOptions.Compiled)),
        ("api-key-assignment", new Regex(@"(?i)\bapi[-_]?key\b[""']?\s*[:=]\s*[""']?[A-Za-z0-9]{16,}", RegexOptions.Compiled)),
        ("openai-style-key", new Regex(@"\bsk-[A-Za-z0-9]{16,}\b", RegexOptions.Compiled)),
    };

    public static IReadOnlyList<ScanFinding> ScanText(string file, string content)
    {
        var findings = new List<ScanFinding>();
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (var (name, rx) in Patterns)
            {
                if (rx.IsMatch(lines[i]))
                {
                    findings.Add(new ScanFinding(file, i + 1, name));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Recursively scans files matching <paramref name="includeExtensions"/>, skipping any path that
    /// contains one of <paramref name="excludeDirSegments"/> (e.g. bin, obj, .venv, tests, .git).
    /// </summary>
    public static IReadOnlyList<ScanFinding> ScanDirectory(
        string root,
        IReadOnlyCollection<string> includeExtensions,
        IReadOnlyCollection<string> excludeDirSegments)
    {
        var findings = new List<ScanFinding>();
        if (!Directory.Exists(root))
        {
            return findings;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (excludeDirSegments.Any(seg => rel.Contains("/" + seg + "/", StringComparison.OrdinalIgnoreCase)
                                              || rel.StartsWith(seg + "/", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (includeExtensions.Count > 0 && !includeExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            findings.AddRange(ScanText(rel, content));
        }

        return findings;
    }
}
