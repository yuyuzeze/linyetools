using KikuCaption.ComponentManagement.Archives;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Downloading;
using KikuCaption.ComponentManagement.Http;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace KikuCaption.ComponentManagement.DependencyInjection;

/// <summary>
/// Composition root for the remote-resource module (R7A.2). This is the ONLY supported way to wire up
/// the manifest client + downloader, and it registers a typed HttpClient whose primary handler has
/// <c>AllowAutoRedirect=false</c>. Because the clients depend on <see cref="IRawHttpTransport"/> (built
/// here over that redirect-free client) rather than a raw HttpClient, a future DI edit cannot silently
/// hand them an auto-redirecting client and bypass the per-hop redirect validation.
/// </summary>
public static class ComponentManagementServiceCollectionExtensions
{
    /// <summary>The named HttpClient used for all remote-resource requests.</summary>
    public const string HttpClientName = "ComponentManagement";

    public static IServiceCollection AddComponentManagement(this IServiceCollection services, RemoteResourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ManifestParser>();
        services.TryAddSingleton(_ => ComponentPathResolver.CreateDefault());

        // The security-critical registration: the primary handler NEVER auto-follows redirects, so
        // SafeHttpRequester always observes each 3xx and validates the hop itself.
        services.AddHttpClient(HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateRedirectFreeHandler);

        // The controlled transport is the only HTTP seam the clients see.
        services.TryAddSingleton<IRawHttpTransport>(sp =>
            new HttpClientRawTransport(() => sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        services.TryAddSingleton<IRemoteManifestClient, RemoteManifestClient>();
        services.TryAddSingleton(sp => new SecureDownloader(
            sp.GetRequiredService<IRawHttpTransport>(),
            sp.GetService<ILogger<SecureDownloader>>(),
            options.MaxRedirects));

        // R7B: safe extraction + the install coordinator. The verifier and replacement guard have
        // safe defaults here; the app replaces them (a model light-load verifier and a worker-in-use
        // guard) with a later registration, which wins at resolve time.
        services.TryAddSingleton<SafeZipExtractor>();
        services.TryAddSingleton<IComponentVerifier, NoOpComponentVerifier>();
        services.TryAddSingleton<IComponentReplacementGuard, AlwaysAllowReplacementGuard>();
        services.TryAddSingleton<IComponentInstaller>(sp => new ComponentInstallCoordinator(
            sp.GetRequiredService<SecureDownloader>(),
            sp.GetRequiredService<SafeZipExtractor>(),
            sp.GetRequiredService<ComponentPathResolver>(),
            options,
            sp.GetRequiredService<IComponentVerifier>(),
            sp.GetRequiredService<IComponentReplacementGuard>(),
            sp.GetService<ILogger<ComponentInstallCoordinator>>()));

        return services;
    }

    /// <summary>
    /// Builds the module's primary HTTP handler with automatic redirects disabled. Exposed (internal)
    /// so tests can assert the guarantee directly.
    /// </summary>
    internal static HttpMessageHandler CreateRedirectFreeHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        MaxAutomaticRedirections = 1 // belt-and-suspenders; irrelevant while AllowAutoRedirect is false
    };
}
