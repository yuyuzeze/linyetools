using System.Net;
using KikuCaption.ComponentManagement.Security;
using Microsoft.Extensions.Logging;

namespace KikuCaption.ComponentManagement.Http;

/// <summary>Thrown when a redirect is rejected by a safety check (downgrade, bad target, loop/limit).</summary>
public sealed class RedirectValidationException : Exception
{
    public RedirectValidationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

/// <summary>
/// Performs an HTTP GET while following redirects UNDER OUR OWN CONTROL (R7A.1) instead of trusting the
/// handler's automatic redirect. Every hop's target is re-validated: it must be an allowed URL, and an
/// HTTPS→HTTP downgrade is always refused (even when insecure HTTP is otherwise permitted). The number
/// of redirects is capped to defeat loops, and each hop is logged only through
/// <see cref="UrlSanitizer"/> so a token in a redirect URL never reaches a log.
///
/// It drives an <see cref="IRawHttpTransport"/> that performs a single, non-redirecting GET per hop.
/// Because the only production transport is built by <c>AddComponentManagement</c> over a handler with
/// <c>AllowAutoRedirect=false</c>, this method is guaranteed to observe each 3xx response rather than
/// have it silently auto-followed — the redirect-off guarantee is structural, not a caller promise.
/// </summary>
public static class SafeHttpRequester
{
    public static async Task<HttpResponseMessage> GetWithControlledRedirectsAsync(
        IRawHttpTransport transport, Uri initialUri, bool allowInsecureHttp, int maxRedirects, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!ResourceSecurity.IsAllowedUrl(initialUri.ToString(), allowInsecureHttp))
        {
            throw new RedirectValidationException("scheme", "Initial URL is not an allowed (HTTPS) URL.");
        }

        var current = initialUri;
        for (int hop = 0; ; hop++)
        {
            var response = await transport.SendGetAsync(current, cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                // A 3xx with no Location is not actionable; hand it back so EnsureSuccessStatusCode fails.
                return response;
            }

            if (hop >= maxRedirects)
            {
                response.Dispose();
                throw new RedirectValidationException("redirect-limit",
                    $"Exceeded the maximum of {maxRedirects} redirects.");
            }

            // Resolve relative Location against the current URL.
            var next = new Uri(current, location);

            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                response.Dispose();
                throw new RedirectValidationException("downgrade",
                    "Refusing an HTTPS→HTTP redirect downgrade.");
            }

            if (!ResourceSecurity.IsAllowedUrl(next.ToString(), allowInsecureHttp))
            {
                response.Dispose();
                throw new RedirectValidationException("scheme",
                    "Redirect target is not an allowed (HTTPS) URL.");
            }

            logger.LogInformation("Following redirect {From} -> {To}.",
                UrlSanitizer.Sanitize(current), UrlSanitizer.Sanitize(next));

            response.Dispose();
            current = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or   // 301
        HttpStatusCode.Found or               // 302
        HttpStatusCode.SeeOther or            // 303
        HttpStatusCode.TemporaryRedirect or   // 307
        HttpStatusCode.PermanentRedirect;     // 308
}
