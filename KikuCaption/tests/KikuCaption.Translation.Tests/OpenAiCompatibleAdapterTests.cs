using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using KikuCaption.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Translation.Tests;

public sealed class OpenAiCompatibleAdapterTests
{
    private const string SampleJa = "今回のリリースについて確認します。";

    private static string OkBody(string content)
        => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static TranslationOptions Opts(
        TranslationAuthMode auth = TranslationAuthMode.Bearer,
        string endpoint = "https://api.example.internal/v1/chat/completions",
        string headerName = "Authorization",
        string apiVersion = "",
        int timeoutSeconds = 30,
        long maxResponseBytes = 512 * 1024,
        int maxInput = 4000)
        => new()
        {
            Enabled = true,
            Endpoint = endpoint,
            Model = "meeting-translate",
            ApiVersion = apiVersion,
            AuthenticationMode = auth,
            HeaderName = headerName,
            TimeoutSeconds = timeoutSeconds,
            MaxResponseBytes = maxResponseBytes,
            MaxInputCharacters = maxInput,
            SourceLanguage = "ja",
            TargetLanguage = "zh"
        };

    private static OpenAiCompatibleTranslationAdapter Adapter(
        FakeHttpMessageHandler handler, TranslationOptions options, out SingleClientFactory factory, string? secret = "test-secret-XYZ")
    {
        factory = new SingleClientFactory(handler);
        return new OpenAiCompatibleTranslationAdapter(
            factory, new FakeSecretStore(secret), options, NullLogger<OpenAiCompatibleTranslationAdapter>.Instance);
    }

    private static async Task<TranslationException> CaptureAsync(Func<Task> action)
        => await Assert.ThrowsAsync<TranslationException>(action);

    [Fact] // adapter 1: Bearer auth
    public async Task Bearer_SetsAuthorizationHeader()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("确认发布内容。"));
        var adapter = Adapter(handler, Opts(TranslationAuthMode.Bearer), out _);

        var result = await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        Assert.Equal("确认发布内容。", result);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-secret-XYZ", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact] // adapter 2: custom api-key header
    public async Task ApiKeyHeader_UsesConfiguredHeaderName()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("你好"));
        var adapter = Adapter(handler, Opts(TranslationAuthMode.ApiKeyHeader, headerName: "api-key"), out _);

        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("api-key", out var values));
        Assert.Equal("test-secret-XYZ", values!.Single());
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact] // adapter 3: no auth
    public async Task None_SendsNoAuthHeaders()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("你好"));
        var adapter = Adapter(handler, Opts(TranslationAuthMode.None), out _, secret: null);

        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.False(handler.LastRequest.Headers.Contains("api-key"));
    }

    [Fact] // adapter 4/5: endpoint + HTTPS validation
    public async Task Endpoint_MustBeHttps_AndNonEmpty()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("x"));

        var httpAdapter = Adapter(handler, Opts(endpoint: "http://insecure.internal/v1"), out _);
        Assert.Equal(TranslationErrorCode.InvalidConfig,
            (await CaptureAsync(() => httpAdapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);

        var emptyAdapter = Adapter(handler, Opts(endpoint: ""), out _);
        Assert.Equal(TranslationErrorCode.InvalidConfig,
            (await CaptureAsync(() => emptyAdapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);

        Assert.Equal(0, handler.CallCount); // never hit the wire
    }

    [Fact] // adapter 6/7/8: request body structure, system/user split, model field
    public async Task RequestBody_HasModel_SystemUserSplit_LowRandomness()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("你好"));
        var adapter = Adapter(handler, Opts(), out _);

        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("meeting-translate", root.GetProperty("model").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(TranslationPrompt.System, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(SampleJa, messages[1].GetProperty("content").GetString());

        // Original text must NOT be baked into the system prompt.
        Assert.DoesNotContain(SampleJa, messages[0].GetProperty("content").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact] // adapter: api-version appended only when configured
    public async Task ApiVersion_AppendedWhenPresent()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("你好"));
        var adapter = Adapter(handler, Opts(apiVersion: "2024-10-01"), out _);

        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        Assert.Contains("api-version=2024-10-01", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact] // adapter 9: normal response
    public async Task NormalResponse_ReturnsTrimmedTranslation()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("  确认一下本次发布内容。  "));
        var adapter = Adapter(handler, Opts(), out _);

        var result = await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);

        Assert.Equal("确认一下本次发布内容。", result);
    }

    [Fact] // adapter 10: empty output
    public async Task EmptyContent_IsInvalidResponse()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("   "));
        var adapter = Adapter(handler, Opts(), out _);

        Assert.Equal(TranslationErrorCode.InvalidResponse,
            (await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);
    }

    [Fact] // adapter 11: non-JSON / error HTML
    public async Task NonJsonBody_IsInvalidResponse()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Gateway Error</body></html>", System.Text.Encoding.UTF8, "text/html")
        }));
        var adapter = Adapter(handler, Opts(), out _);

        Assert.Equal(TranslationErrorCode.InvalidResponse,
            (await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);
    }

    [Fact] // adapter 12: oversized response rejected
    public async Task OversizedResponse_IsInvalidResponse()
    {
        var big = OkBody(new string('测', 5000));
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, big);
        var adapter = Adapter(handler, Opts(maxResponseBytes: 64), out _);

        Assert.Equal(TranslationErrorCode.InvalidResponse,
            (await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);
    }

    [Fact] // adapter: input length limit
    public async Task OverlongInput_IsRejectedWithoutCall()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("x"));
        var adapter = Adapter(handler, Opts(maxInput: 10), out _);

        var ex = await CaptureAsync(() => adapter.TranslateAsync(new string('あ', 50), "ja", "zh", CancellationToken.None));
        Assert.Equal(TranslationErrorCode.InputTooLong, ex.Code);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact] // adapter 13: timeout
    public async Task Timeout_MapsToTimeoutCode()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = Adapter(handler, Opts(timeoutSeconds: 1), out _);

        var ex = await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None));
        Assert.Equal(TranslationErrorCode.Timeout, ex.Code);
    }

    [Fact] // adapter 14: cancellation
    public async Task Cancellation_MapsToCancelledCode()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var adapter = Adapter(handler, Opts(), out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", cts.Token));
        Assert.Equal(TranslationErrorCode.Cancelled, ex.Code);
    }

    [Theory] // adapter 15/16/17/19: status classification
    [InlineData(400, TranslationErrorCode.BadRequest)]
    [InlineData(401, TranslationErrorCode.Auth)]
    [InlineData(403, TranslationErrorCode.Auth)]
    [InlineData(500, TranslationErrorCode.ServiceUnavailable)]
    [InlineData(502, TranslationErrorCode.ServiceUnavailable)]
    [InlineData(503, TranslationErrorCode.ServiceUnavailable)]
    public async Task HttpErrors_MapToCodes(int status, TranslationErrorCode expected)
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var adapter = Adapter(handler, Opts(), out _);

        Assert.Equal(expected, (await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);
    }

    [Fact] // adapter 18: 429 + Retry-After
    public async Task RateLimited_ParsesRetryAfter()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(resp);
        });
        var adapter = Adapter(handler, Opts(), out _);

        var ex = await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None));
        Assert.Equal(TranslationErrorCode.RateLimited, ex.Code);
        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(7, ex.RetryAfter!.Value.TotalSeconds, 0);
    }

    [Fact] // adapter 20: network error
    public async Task NetworkError_MapsToNetworkCode()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        var adapter = Adapter(handler, Opts(), out _);

        Assert.Equal(TranslationErrorCode.Network,
            (await CaptureAsync(() => adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None))).Code);
    }

    [Fact] // adapter 21/22: secret never in body; client is reused (not per-call)
    public async Task Secret_NotInBody_ClientReused()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, OkBody("你好"));
        var adapter = Adapter(handler, Opts(), out var factory);

        var c1 = factory.CreateClient("translation");
        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);
        await adapter.TranslateAsync(SampleJa, "ja", "zh", CancellationToken.None);
        var c2 = factory.CreateClient("translation");

        Assert.DoesNotContain("test-secret-XYZ", handler.LastRequestBody);
        Assert.Same(c1, c2); // same reusable client instance
    }
}
