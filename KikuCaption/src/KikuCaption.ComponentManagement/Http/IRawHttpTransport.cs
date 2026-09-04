namespace KikuCaption.ComponentManagement.Http;

/// <summary>
/// The ONLY HTTP seam the manifest client and downloader depend on (R7A.2). It performs a SINGLE GET
/// and returns the raw response WITHOUT following any redirect — redirect handling is done, per hop,
/// by <see cref="SafeHttpRequester"/>. Depending on this interface (instead of a raw
/// <see cref="System.Net.Http.HttpClient"/>) removes the public "pass me any HttpClient" entry point
/// through which a future DI change could silently re-enable automatic redirects and bypass the
/// per-hop validation. The production implementation is internal and is built only by
/// <c>AddComponentManagement</c> over a handler with <c>AllowAutoRedirect=false</c>.
/// </summary>
public interface IRawHttpTransport
{
    /// <summary>Issues one GET (response-headers-read) and returns the raw response — no auto-redirect.</summary>
    Task<HttpResponseMessage> SendGetAsync(Uri uri, CancellationToken cancellationToken);
}
