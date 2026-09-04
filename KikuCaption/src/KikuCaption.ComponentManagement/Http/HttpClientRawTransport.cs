namespace KikuCaption.ComponentManagement.Http;

/// <summary>
/// The production <see cref="IRawHttpTransport"/> (R7A.2). Internal by design: it is created ONLY by
/// <c>AddComponentManagement</c>, which supplies a factory for the module's typed HttpClient whose
/// primary handler has <c>AllowAutoRedirect=false</c>. There is no public constructor that could take
/// an arbitrary (possibly redirect-following) HttpClient. Tests reach it through InternalsVisibleTo.
/// </summary>
internal sealed class HttpClientRawTransport : IRawHttpTransport
{
    private readonly Func<HttpClient> _clientFactory;

    /// <summary>Backed by a factory so an IHttpClientFactory-managed client is created per request.</summary>
    internal HttpClientRawTransport(Func<HttpClient> clientFactory) => _clientFactory = clientFactory;

    /// <summary>Convenience for tests: a fixed (redirect-free) client.</summary>
    internal HttpClientRawTransport(HttpClient client) : this(() => client) { }

    public Task<HttpResponseMessage> SendGetAsync(Uri uri, CancellationToken cancellationToken)
        => _clientFactory().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
}
