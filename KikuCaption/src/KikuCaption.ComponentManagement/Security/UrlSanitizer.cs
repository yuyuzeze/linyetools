namespace KikuCaption.ComponentManagement.Security;

/// <summary>
/// Produces a log-safe form of a URL: scheme + host + path only, with the query string and any
/// user-info dropped. This keeps SAS tokens / access keys / auth query parameters out of logs
/// (R7A: "日志不记录 URL 中的 query token").
/// </summary>
public static class UrlSanitizer
{
    public static string Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(none)";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Not a parseable absolute URL — never echo it back verbatim (it might carry a token).
            return "(redacted)";
        }

        // GetLeftPart(Path) = scheme://host[:port]/path, without query or fragment. Host authority
        // from a well-formed URI does not include user-info here because we rebuild from components.
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{host}{uri.AbsolutePath}";
    }

    public static string Sanitize(Uri? uri) => uri is null ? "(none)" : Sanitize(uri.ToString());
}
