using System.Net;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.DependencyInjection;
using KikuCaption.ComponentManagement.Http;
using KikuCaption.ComponentManagement.Manifest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

/// <summary>
/// R7A.2: proves the redirect-off guarantee is STRUCTURAL. Uses a real DI container and the real
/// HttpClientFactory configuration produced by <c>AddComponentManagement</c>.
/// </summary>
public class RedirectFreeDiTests
{
    private const string ManifestJson =
        """{ "schemaVersion": 1, "product": "KikuMemo", "channel": "stable", "components": [] }""";

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static RemoteResourceOptions Options() => new()
    {
        Enabled = true,
        ManifestUrl = "https://internal.example/manifest.json"
    };

    [Fact] // R7A.2: the module's own handler factory disables auto-redirect
    public void ModuleHandlerFactory_DisablesAutoRedirect()
    {
        var handler = ComponentManagementServiceCollectionExtensions.CreateRedirectFreeHandler();
        var http = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(http.AllowAutoRedirect);
    }

    [Fact] // R7A.2: a plain AddComponentManagement registration configures the named client redirect-free.
    // This is the "normal registration cannot silently enable auto-redirect" proof.
    public void NormalRegistration_ConfiguresPrimaryHandler_RedirectFree()
    {
        var services = new ServiceCollection();
        services.AddComponentManagement(Options());
        using var provider = services.BuildServiceProvider();

        var factoryOptions = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(ComponentManagementServiceCollectionExtensions.HttpClientName);

        // Run the configured builder actions against a probe builder and inspect the primary handler.
        var builder = new ProbeHandlerBuilder();
        foreach (var action in factoryOptions.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        var primary = Assert.IsType<HttpClientHandler>(builder.PrimaryHandler);
        Assert.False(primary.AllowAutoRedirect); // could not be silently turned on
    }

    [Fact] // R7A.2: through real DI, a 302 reaches SafeHttpRequester and an HTTPS→HTTPS hop is followed
    public async Task ThroughDi_HttpsToHttps_Followed()
    {
        var fake = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath == "/manifest.json"
            ? FakeHttpMessageHandler.Redirect("https://cdn.example/real-manifest.json")
            : Ok(ManifestJson));

        using var provider = BuildProviderWithTerminal(fake);
        var client = provider.GetRequiredService<IRemoteManifestClient>();

        var manifest = await client.GetConfiguredManifestAsync(CancellationToken.None);

        Assert.Equal("KikuMemo", manifest!.Product);
        Assert.Equal(2, fake.RequestCount); // our controlled logic followed the hop (not auto-redirect)
    }

    [Fact] // R7A.2: through real DI, an HTTPS→HTTP downgrade redirect is rejected per-hop
    public async Task ThroughDi_HttpsToHttp_Rejected()
    {
        var fake = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath == "/manifest.json"
            ? FakeHttpMessageHandler.Redirect("http://cdn.example/insecure")
            : Ok(ManifestJson));

        using var provider = BuildProviderWithTerminal(fake);
        var client = provider.GetRequiredService<IRemoteManifestClient>();

        var ex = await Assert.ThrowsAsync<RedirectValidationException>(
            () => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal("downgrade", ex.Code);
    }

    // Builds a real provider from AddComponentManagement, then swaps ONLY the terminal handler for a
    // fake so we can script 3xx responses. The redirect-following remains our controlled logic.
    private static ServiceProvider BuildProviderWithTerminal(HttpMessageHandler terminal)
    {
        var services = new ServiceCollection();
        services.AddComponentManagement(Options());
        services.AddHttpClient(ComponentManagementServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => terminal);
        return services.BuildServiceProvider();
    }

    // Minimal concrete HttpMessageHandlerBuilder to replay the configured builder actions.
    private sealed class ProbeHandlerBuilder : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
