using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Manifest;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class RemoteManifestClientTests
{
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";
    private static string ValidManifest() => $$"""
        { "schemaVersion": 1, "product": "KikuMemo", "channel": "stable",
          "components": [ { "id": "whisper-small", "type": "WhisperModel", "version": "1",
            "url": "https://internal.example/m.zip", "sha256": "{{Sha}}", "sizeBytes": 10,
            "installDirectory": "models/whisper/small", "requiredFiles": ["model.bin"] } ] }
        """;

    [Fact] // R7A: disabled config performs NO network request
    public async Task Disabled_MakesNoRequest()
    {
        var handler = FakeHttpMessageHandler.WithString(ValidManifest());
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = false, ManifestUrl = "https://internal.example/manifest.json" });

        var result = await client.GetConfiguredManifestAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact] // enabled + no URL performs no request
    public async Task Enabled_NoUrl_MakesNoRequest()
    {
        var handler = FakeHttpMessageHandler.WithString(ValidManifest());
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = null });

        Assert.Null(await client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact] // a configured HTTPS manifest is fetched and parsed
    public async Task Configured_FetchesAndParses()
    {
        var handler = FakeHttpMessageHandler.WithString(ValidManifest());
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json" });

        var m = await client.GetConfiguredManifestAsync(CancellationToken.None);

        Assert.NotNull(m);
        Assert.Equal("KikuMemo", m!.Product);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact] // a non-HTTPS configured URL is rejected without a request
    public async Task NonHttpsUrl_Rejected_NoRequest()
    {
        var handler = FakeHttpMessageHandler.WithString(ValidManifest());
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "http://internal.example/manifest.json" });

        await Assert.ThrowsAsync<ManifestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact] // a manifest larger than the cap (declared Content-Length) is rejected
    public async Task OversizedManifest_Rejected()
    {
        var big = new string('x', 5000);
        var handler = FakeHttpMessageHandler.WithString(big);
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json", MaxManifestBytes = 1024 });

        var ex = await Assert.ThrowsAsync<ManifestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal("size", ex.Code);
    }

    [Fact] // a body that lies about its (small) length is still capped while streaming
    public async Task LyingLength_StillCapped()
    {
        var actual = new byte[4000];
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new LyingLengthContent(actual, declaredLength: 10) }));
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json", MaxManifestBytes = 1024 });

        var ex = await Assert.ThrowsAsync<ManifestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal("size", ex.Code);
    }

    [Fact] // R7A.1: a manifest for a different product is rejected (identity gate) — no components trusted
    public async Task WrongProduct_Rejected()
    {
        var json = ValidManifest().Replace("\"KikuMemo\"", "\"SomeOtherApp\"");
        var handler = FakeHttpMessageHandler.WithString(json);
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json" });

        var ex = await Assert.ThrowsAsync<ManifestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal("product", ex.Code);
    }

    [Fact] // R7A.1: a manifest for a different channel is rejected
    public async Task WrongChannel_Rejected()
    {
        var handler = FakeHttpMessageHandler.WithString(ValidManifest());
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json", Channel = "beta" });

        var ex = await Assert.ThrowsAsync<ManifestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
        Assert.Equal("channel", ex.Code);
    }

    [Fact] // R7A: a network failure surfaces as a catchable exception, never an unobserved crash
    public async Task NetworkFailure_IsCatchable()
    {
        var handler = FakeHttpMessageHandler.Throwing();
        var client = new RemoteManifestClient(handler.CreateTransport(),
            new RemoteResourceOptions { Enabled = true, ManifestUrl = "https://internal.example/manifest.json" });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetConfiguredManifestAsync(CancellationToken.None));
    }
}
