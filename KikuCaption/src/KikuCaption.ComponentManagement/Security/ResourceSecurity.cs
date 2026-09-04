using System.Text.RegularExpressions;

namespace KikuCaption.ComponentManagement.Security;

/// <summary>
/// Central, side-effect-free security checks shared by the manifest parser, downloader and extractor
/// (R7A): URL scheme, SHA-256 shape, relative install-path safety, and URL log sanitization. Kept in
/// one place so every remote-resource path applies the same rules.
/// </summary>
public static partial class ResourceSecurity
{
    /// <summary>
    /// Validates a download/manifest URL. Only absolute HTTPS is accepted by default; plain HTTP is
    /// allowed only when the caller explicitly opted in (a trusted internal test). file://, ftp://,
    /// UNC and relative URLs are always rejected.
    /// </summary>
    public static bool IsAllowedUrl(string? url, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp;
    }

    /// <summary>A SHA-256 hex digest is exactly 64 hex characters.</summary>
    public static bool IsValidSha256(string? hex)
        => !string.IsNullOrWhiteSpace(hex) && Sha256Pattern().IsMatch(hex.Trim());

    /// <summary>
    /// A component's install directory must be a safe RELATIVE path: not empty, not rooted/absolute,
    /// no drive or UNC prefix, and no "." / ".." segment that could escape the install root.
    /// </summary>
    public static bool IsSafeRelativeInstallPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Reject absolute (C:\, \\server, /root) and rooted paths outright.
        if (Path.IsPathRooted(path) || Path.IsPathFullyQualified(path))
        {
            return false;
        }

        // A colon anywhere implies a drive/ADS reference (e.g. "C:foo", "x:stream").
        if (path.Contains(':'))
        {
            return false;
        }

        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies that <paramref name="candidateFullPath"/> stays inside <paramref name="rootFullPath"/>
    /// after normalization — the last line of defense against Zip-Slip / traversal. Both inputs should
    /// already be absolute; the comparison is case-insensitive with a trailing separator so
    /// "root" cannot match "rootEvil".
    /// </summary>
    public static bool IsContainedWithin(string rootFullPath, string candidateFullPath)
    {
        var root = Path.GetFullPath(rootFullPath);
        var candidate = Path.GetFullPath(candidateFullPath);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
