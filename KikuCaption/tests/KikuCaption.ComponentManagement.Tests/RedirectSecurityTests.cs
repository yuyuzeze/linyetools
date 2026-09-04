using System.Net;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Http;
using KikuCaption.ComponentManagement.Manifest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class RedirectSecurityTests
{
    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact] // R7A.1: an HTTPS→HTTPS redirect is followed to the final response
    public async Task HttpsToHttps_Followed()
    {
        var handler = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath switch
        {
            "/m" => FakeHttpMessageHandler.Redirect("https://b.example/final"),
            "/final" => Ok("FINAL-BODY"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        using var response = await SafeHttpRequester.GetWithControlledRedirectsAsync(
            handler.CreateTransport(), new Uri("https://a.example/m"), allowInsecureHttp: false,
            maxRedirects: 5, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("FINAL-BODY", await response.Content.ReadAsStringAsync());
    }

    [Fact] // an HTTPS→HTTP downgrade redirect is refused
    public async Task HttpsToHttp_Downgrade_Rejected()
    {
        var handler = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath == "/m"
            ? FakeHttpMessageHandler.Redirect("http://a.example/insecure")
            : Ok("should-not-reach"));

        var ex = await Assert.ThrowsAsync<RedirectValidationException>(() =>
            SafeHttpRequester.GetWithControlledRedirectsAsync(
                handler.CreateTransport(), new Uri("https://a.example/m"), allowInsecureHttp: true, // even when http is allowed
                maxRedirects: 5, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("downgrade", ex.Code);
    }

    [Fact] // a redirect loop / exceeding the limit is refused
    public async Task RedirectLoop_ExceedsLimit_Rejected()
    {
        // Always redirect back to the same URL → the hop counter reaches the limit.
        var handler = FakeHttpMessageHandler.ByUrl(_ => FakeHttpMessageHandler.Redirect("https://a.example/loop"));

        var ex = await Assert.ThrowsAsync<RedirectValidationException>(() =>
            SafeHttpRequester.GetWithControlledRedirectsAsync(
                handler.CreateTransport(), new Uri("https://a.example/loop"), allowInsecureHttp: false,
                maxRedirects: 3, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("redirect-limit", ex.Code);
    }

    [Fact] // R7A.1: redirect URLs are logged with query + user-info stripped
    public async Task RedirectUrls_AreLogSanitized()
    {
        var handler = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath == "/m"
            ? FakeHttpMessageHandler.Redirect("https://b.example/final?token=SECRET2")
            : Ok("ok"));
        var logger = new CapturingLogger<SecureDownloaderMarker>();

        using var _ = await SafeHttpRequester.GetWithControlledRedirectsAsync(
            handler.CreateTransport(), new Uri("https://a.example/m?token=SECRET1"), allowInsecureHttp: false,
            maxRedirects: 5, logger, CancellationToken.None);

        var joined = string.Join("\n", logger.Messages);
        Assert.Contains("a.example", joined);
        Assert.Contains("b.example", joined);
        Assert.DoesNotContain("SECRET1", joined);
        Assert.DoesNotContain("SECRET2", joined);
        Assert.DoesNotContain("token", joined);
    }

    [Fact] // integration: the manifest client fetches through one HTTPS redirect and parses
    public async Task ManifestClient_FollowsHttpsRedirect()
    {
        var manifestJson = $$"""
            { "schemaVersion": 1, "product": "KikuMemo", "channel": "stable", "components": [] }
            """;
        var handler = FakeHttpMessageHandler.ByUrl(uri => uri.AbsolutePath == "/manifest.json"
            ? FakeHttpMessageHandler.Redirect("https://cdn.example/real-manifest.json")
            : Ok(manifestJson));
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json" });

        var manifest = await client.GetConfiguredManifestAsync(CancellationToken.None);
        Assert.Equal("KikuMemo", manifest!.Product);
    }

    // Marker type only to parameterize the generic capturing logger.
    internal sealed class SecureDownloaderMarker { }
}
