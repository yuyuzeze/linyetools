using System.Linq;
using System.Net;
using KikuCaption.Core.Enums;
using KikuCaption.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Summarization.Tests;

/// <summary>UI-R5C: the summary HTTP client — request shape, retry/repair, errors, cancellation.</summary>
public class SummaryClientTests
{
    private static TranslationOptions Transport(TranslationAuthMode mode = TranslationAuthMode.Bearer) => new()
    {
        Endpoint = "https://api.example.com/v1/chat/completions",
        Model = "translation-model",
        AuthenticationMode = mode
    };

    private static MeetingSummaryRequest Request(string model = "summary-model", string lang = "zh", MeetingType type = MeetingType.SinglePresenter)
        => new()
        {
            SessionId = Guid.NewGuid(),
            SessionDirectory = @"C:\sessions\s1",
            MeetingType = type,
            OutputLanguage = lang,
            Model = model,
            PromptVersion = MeetingSummaryPrompt.Version,
            SourceLanguage = "ja",
            Segments = new[] { new MeetingSummarySegment(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "確認済みの字幕テキスト") }
        };

    private static MeetingSummaryChunk Chunk(string text)
        => new(0, TimeSpan.Zero, TimeSpan.FromSeconds(2), new[] { new MeetingSummarySegment(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), text) }, false);

    private static OpenAiCompatibleSummaryClient Client(FakeHttpMessageHandler handler, TranslationOptions? transport = null, MeetingSummaryOptions? opts = null, FakeSecretStore? secrets = null)
        => new(new SingleClientFactory(handler), secrets ?? new FakeSecretStore(), transport ?? Transport(),
            opts ?? new MeetingSummaryOptions { MaxRetries = 2 }, NullLogger<OpenAiCompatibleSummaryClient>.Instance);

    [Fact] // request carries the model, a system+user split, and ONLY the caption text
    public async Task Map_SendsModelSystemUserAndCaptionOnly()
    {
        var handler = new FakeHttpMessageHandler().EnqueueChat("{\"overview\":\"o\"}");
        await Client(handler).MapAsync(Request(model: "summary-model"), Chunk("確認済みの字幕テキスト"), CancellationToken.None);

        var body = handler.RequestBodies.Single();
        Assert.Contains("\"model\":\"summary-model\"", body);
        Assert.Contains("\"role\":\"system\"", body);
        Assert.Contains("\"role\":\"user\"", body);
        Assert.Contains("\"max_tokens\":2000", body);
        Assert.Contains("確認済みの字幕テキスト", body); // the confirmed caption
        Assert.DoesNotContain("partial", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".mp4", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".wav", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BadRequest_ExposesOnlySanitizedProviderCodes()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"code":"context_length_exceeded","type":"invalid_request_error","param":"messages","message":"sensitive provider text"}}""")
        });

        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() =>
            Client(handler).MapAsync(Request(), Chunk("t"), CancellationToken.None));

        Assert.Equal(TranslationErrorCode.BadRequest, ex.Code);
        Assert.Equal("code=context_length_exceeded,type=invalid_request_error,param=messages", ex.SafeDetail);
        Assert.DoesNotContain("sensitive", ex.SafeDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact] // scenario 34: 401 is not retried
    public async Task Auth401_NotRetried()
    {
        var handler = new FakeHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() => Client(handler).MapAsync(Request(), Chunk("t"), CancellationToken.None));
        Assert.Equal(TranslationErrorCode.Auth, ex.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact] // scenario 36: 5xx is retried within the limit, then succeeds
    public async Task ServerError_RetriedThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueChat("{\"overview\":\"ok\"}");
        var opts = new MeetingSummaryOptions { MaxRetries = 2 };
        var sections = await Client(handler, opts: opts).MapAsync(Request(), Chunk("t"), CancellationToken.None);
        Assert.Equal("ok", sections.Overview);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact] // scenario 35: 429 honors Retry-After and retries
    public async Task RateLimited_RetriesWithRetryAfter()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, r => r.Headers.TryAddWithoutValidation("Retry-After", "0"))
            .EnqueueChat("{\"overview\":\"ok\"}");
        var sections = await Client(handler, opts: new MeetingSummaryOptions { MaxRetries = 1 }).MapAsync(Request(), Chunk("t"), CancellationToken.None);
        Assert.Equal("ok", sections.Overview);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact] // scenario 26: a non-JSON response triggers exactly one format repair, then parses
    public async Task NonJson_UsesPlainTextWithoutRepair()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueChat("Sure! Here is the summary in prose.");
        var sections = await Client(handler).MapAsync(Request(), Chunk("t"), CancellationToken.None);
        Assert.Equal("Sure! Here is the summary in prose.", sections.Overview);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact] // scenario 27: if repair still fails, it fails safely (no infinite retry)
    public async Task EmptyResponse_ThrowsInvalidResponseWithoutRepair()
    {
        var handler = new FakeHttpMessageHandler().EnqueueChat("   ");
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() => Client(handler).MapAsync(Request(), Chunk("t"), CancellationToken.None));
        Assert.Equal(TranslationErrorCode.InvalidResponse, ex.Code);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact] // scenario 28: an over-large response body is rejected
    public async Task OversizeResponse_Rejected()
    {
        var big = FakeHttpMessageHandler.ChatJson("{\"overview\":\"" + new string('x', 5000) + "\"}");
        var handler = new FakeHttpMessageHandler().Enqueue(_ => FakeHttpMessageHandler.Ok(big));
        var opts = new MeetingSummaryOptions { MaxResponseBytes = 1000, MaxRetries = 0 };
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() => Client(handler, opts: opts).MapAsync(Request(), Chunk("t"), CancellationToken.None));
        Assert.Equal(TranslationErrorCode.InvalidResponse, ex.Code);
    }

    [Fact] // scenario 37: a cancelled token cancels the request
    public async Task Cancel_Throws()
    {
        var handler = new FakeHttpMessageHandler().EnqueueChat("{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() => Client(handler).MapAsync(Request(), Chunk("t"), cts.Token));
        Assert.Equal(TranslationErrorCode.Cancelled, ex.Code);
    }

    [Fact] // scenario 33: no API key configured → InvalidConfig (via DPAPI store), not retried
    public async Task NoApiKey_InvalidConfig()
    {
        var handler = new FakeHttpMessageHandler().EnqueueChat("{}");
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() =>
            Client(handler, secrets: new FakeSecretStore(null)).MapAsync(Request(), Chunk("t"), CancellationToken.None));
        Assert.Equal(TranslationErrorCode.InvalidConfig, ex.Code);
    }

    [Fact] // non-HTTPS endpoint is rejected before any call
    public async Task NonHttps_Rejected()
    {
        var handler = new FakeHttpMessageHandler();
        var transport = new TranslationOptions { Endpoint = "http://insecure.example.com", Model = "m" };
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() => Client(handler, transport).MapAsync(Request(), Chunk("t"), CancellationToken.None));
        Assert.Equal(TranslationErrorCode.InvalidConfig, ex.Code);
        Assert.Equal(0, handler.CallCount);
    }
}
