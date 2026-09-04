using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Http;
using KikuCaption.ComponentManagement.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>
/// Default <see cref="IRemoteManifestClient"/>. Enforces HTTPS (unless the config opted into insecure
/// HTTP for a trusted internal test), caps the response size, applies the configured timeout, and
/// parses strictly. All logging goes through <see cref="UrlSanitizer"/> so a token in the query string
/// is never written to a log.
/// </summary>
public sealed class RemoteManifestClient : IRemoteManifestClient
{
    private readonly IRawHttpTransport _transport;
    private readonly RemoteResourceOptions _options;
    private readonly ManifestParser _parser;
    private readonly ILogger<RemoteManifestClient> _logger;

    public RemoteManifestClient(
        IRawHttpTransport transport,
        RemoteResourceOptions options,
        ManifestParser? parser = null,
        ILogger<RemoteManifestClient>? logger = null)
    {
        _transport = transport;
        _options = options;
        _parser = parser ?? new ManifestParser();
        _logger = logger ?? NullLogger<RemoteManifestClient>.Instance;
    }

    public async Task<RemoteManifest?> GetConfiguredManifestAsync(CancellationToken cancellationToken)
    {
        // Disabled or unconfigured => make NO network request at all.
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ManifestUrl))
        {
            _logger.LogDebug("Remote resources disabled or no manifest URL; skipping fetch.");
            return null;
        }

        if (!ResourceSecurity.IsAllowedUrl(_options.ManifestUrl, _options.AllowInsecureHttp))
        {
            throw new ManifestException("url", "Configured ManifestUrl must be an HTTPS URL.");
        }

        return await GetManifestAsync(new Uri(_options.ManifestUrl), cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteManifest> GetManifestAsync(Uri manifestUri, CancellationToken cancellationToken)
    {
        if (!ResourceSecurity.IsAllowedUrl(manifestUri.ToString(), _options.AllowInsecureHttp))
        {
            throw new ManifestException("url", "Manifest URL must be an HTTPS URL.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 600)));

        _logger.LogInformation("Fetching manifest from {Url}.", UrlSanitizer.Sanitize(manifestUri));

        // R7A.1: follow redirects under our own control, re-validating every hop (no HTTPS→HTTP).
        using var response = await SafeHttpRequester
            .GetWithControlledRedirectsAsync(_transport, manifestUri, _options.AllowInsecureHttp,
                _options.MaxRedirects, _logger, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Reject an oversized manifest up front when the server declares its length.
        if (response.Content.Headers.ContentLength is { } declared && declared > _options.MaxManifestBytes)
        {
            throw new ManifestException("size", "Manifest exceeds the maximum allowed size.");
        }

        var json = await ReadCappedAsync(response, _options.MaxManifestBytes, timeout.Token).ConfigureAwait(false);
        var manifest = _parser.Parse(json, _options.AllowInsecureHttp);

        // R7A.1: identity gate — a manifest for another product/channel is rejected here, so NO
        // component download is ever attempted against it.
        ManifestValidator.ValidateIdentity(manifest, _options.ExpectedProduct, _options.Channel);
        return manifest;
    }

    // Reads the response body as text, aborting if it grows past the cap even when Content-Length lied.
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, long cap, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new ManifestException("size", "Manifest exceeds the maximum allowed size.");
            }

            limited.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
    }
}
