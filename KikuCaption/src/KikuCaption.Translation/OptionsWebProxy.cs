using System.Net;

namespace KikuCaption.Translation;

/// <summary>
/// An <see cref="IWebProxy"/> that reads the proxy from <see cref="TranslationOptions.Proxy"/> at
/// call time, so the setting can be changed at runtime without recreating the pooled HttpClient.
/// When no explicit proxy is configured it delegates to the Windows system proxy (preserving the
/// previous default behavior).
/// </summary>
public sealed class OptionsWebProxy : IWebProxy
{
    private readonly TranslationOptions _options;

    public OptionsWebProxy(TranslationOptions options) => _options = options;

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
    {
        if (Uri.TryCreate(_options.Proxy, UriKind.Absolute, out var explicitProxy))
        {
            return explicitProxy;
        }

        return HttpClient.DefaultProxy?.GetProxy(destination);
    }

    public bool IsBypassed(Uri host)
    {
        if (Uri.TryCreate(_options.Proxy, UriKind.Absolute, out _))
        {
            return false; // an explicit proxy is set → use it for everything
        }

        return HttpClient.DefaultProxy?.IsBypassed(host) ?? true;
    }
}
