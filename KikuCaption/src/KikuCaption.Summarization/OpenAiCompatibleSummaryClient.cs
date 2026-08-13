using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KikuCaption.Core.Enums;
using KikuCaption.Translation;
using KikuCaption.Translation.Security;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Summarization;

/// <summary>The Map/Reduce AI calls, isolated from transport specifics. Independent of the timeline/UI.</summary>
public interface IMeetingSummaryClient
{
    /// <summary>Summarize one chunk of captions into structured sections (Map).</summary>
    Task<MeetingSummarySections> MapAsync(MeetingSummaryRequest request, MeetingSummaryChunk chunk, CancellationToken cancellationToken);

    /// <summary>Merge several partial sections into one (Reduce), in time order.</summary>
    Task<MeetingSummarySections> ReduceAsync(MeetingSummaryRequest request, IReadOnlyList<MeetingSummarySections> parts, CancellationToken cancellationToken);
}

/// <summary>
/// OpenAI-compatible Chat Completions client for meeting summaries (UI-R5C §6/§7). A dedicated
/// client with its own task lifecycle — it does NOT touch TranslationQueue/adapter — but it reuses the
/// company API transport config (<see cref="TranslationOptions"/>: endpoint, auth mode, header, proxy,
/// api-version), the DPAPI <see cref="ITranslationSecretStore"/>, the shared HttpClient, and the error
/// classifier + backoff. System and user messages are strictly separated; captions go only in the user
/// message. One controlled format-repair is attempted on a non-JSON response, then it fails safely.
/// Logs only model / prompt version / sizes / error code — never the prompt or caption text.
/// </summary>
public sealed class OpenAiCompatibleSummaryClient : IMeetingSummaryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITranslationSecretStore _secrets;
    private readonly TranslationOptions _transport;
    private readonly MeetingSummaryOptions _options;
    private readonly ILogger<OpenAiCompatibleSummaryClient> _logger;
    private readonly Random _rng = new();

    // Keep CJK/original text readable (and smaller) on the wire rather than \uXXXX-escaped.
    private static readonly JsonSerializerOptions BodyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public OpenAiCompatibleSummaryClient(
        IHttpClientFactory httpClientFactory,
        ITranslationSecretStore secrets,
        TranslationOptions transport,
        MeetingSummaryOptions options,
        ILogger<OpenAiCompatibleSummaryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _transport = transport;
        _options = options;
        _logger = logger;
    }

    public Task<MeetingSummarySections> MapAsync(MeetingSummaryRequest request, MeetingSummaryChunk chunk, CancellationToken cancellationToken)
    {
        ValidateConfig(request);
        _logger.LogInformation("Summary Map: model={Model} promptV={V} chunk={Idx} chars={Chars}.",
            request.Model, request.PromptVersion, chunk.Index, chunk.CharCount);
        var system = MeetingSummaryPrompt.BuildMapSystem(request.MeetingType, request.OutputLanguage);
        return SendSingleAsync(request, system, chunk.Text, maxTokens: 2000, cancellationToken);
    }

    public Task<MeetingSummarySections> ReduceAsync(MeetingSummaryRequest request, IReadOnlyList<MeetingSummarySections> parts, CancellationToken cancellationToken)
    {
        ValidateConfig(request);
        var user = MeetingSummaryJson.SerializeParts(parts);
        _logger.LogInformation("Summary Reduce: model={Model} promptV={V} parts={Parts} chars={Chars}.",
            request.Model, request.PromptVersion, parts.Count, user.Length);
        var system = MeetingSummaryPrompt.BuildReduceSystem(request.MeetingType, request.OutputLanguage);
        return SendSingleAsync(request, system, user, maxTokens: 1600, cancellationToken);
    }

    private void ValidateConfig(MeetingSummaryRequest request)
    {
        if (!MeetingSummaryPrompt.IsSupported(request.PromptVersion))
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, $"不支持的摘要 Prompt 版本 {request.PromptVersion}。");
        }
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "摘要 Model 未配置。");
        }
        if (string.IsNullOrWhiteSpace(_transport.Endpoint) || !Uri.TryCreate(_transport.Endpoint, UriKind.Absolute, out var uri))
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "API Endpoint 未配置或无效。");
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "API Endpoint 必须使用 HTTPS。");
        }
    }

    // Send, parse, and on a non-JSON response attempt exactly ONE controlled format repair.
    private async Task<MeetingSummarySections> SendSingleAsync(MeetingSummaryRequest request, string system, string user, int maxTokens, CancellationToken ct)
    {
        var content = await SendWithRetryAsync(request, system, user, maxTokens, ct).ConfigureAwait(false);
        if (MeetingSummaryJson.TryParse(content, out var sections))
        {
            return sections;
        }

        _logger.LogWarning("Summary response was not structured JSON; using bounded plain text as overview without a repair request.");
        return MeetingSummaryJson.FromPlainText(content);
    }

    // Retry loop for transient errors only (network/timeout/429/5xx), with backoff + Retry-After.
    private async Task<string> SendWithRetryAsync(MeetingSummaryRequest request, string system, string user, int maxTokens, CancellationToken ct)
    {
        int maxRetries = _options.EffectiveMaxRetries;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(request, system, user, maxTokens, ct).ConfigureAwait(false);
            }
            catch (MeetingSummaryException ex) when (IsRetryable(ex.Code) && attempt < maxRetries)
            {
                var delay = TranslationBackoff.ComputeDelay(attempt + 1, ex.RetryAfter, _rng);
                _logger.LogWarning("Summary request failed ({Code}); retry {Attempt}/{Max} in {Delay}s.",
                    ex.Code, attempt + 1, maxRetries, (int)delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(TranslationErrorCode code)
        => code is TranslationErrorCode.Network or TranslationErrorCode.Timeout
            or TranslationErrorCode.RateLimited or TranslationErrorCode.ServiceUnavailable;

    private async Task<string> SendOnceAsync(MeetingSummaryRequest request, string system, string user, int maxTokens, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            throw new MeetingSummaryException(TranslationErrorCode.Cancelled, "摘要已取消。");
        }

        var baseUri = new Uri(_transport.Endpoint, UriKind.Absolute);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(baseUri))
        {
            Content = new StringContent(BuildBody(request.Model, system, user, maxTokens), Encoding.UTF8, "application/json")
        };
        ApplyAuth(httpRequest);

        var client = _httpClientFactory.CreateClient(OpenAiCompatibleTranslationAdapter.HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled → Cancelled; otherwise the per-request timeout fired → Timeout.
            throw ct.IsCancellationRequested
                ? new MeetingSummaryException(TranslationErrorCode.Cancelled, "摘要已取消。")
                : new MeetingSummaryException(TranslationErrorCode.Timeout, "摘要请求超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new MeetingSummaryException(TranslationErrorCode.Network, "网络错误。", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = TranslationErrorClassifier.FromStatus(response.StatusCode);
                TimeSpan? retryAfter = code == TranslationErrorCode.RateLimited ? ParseRetryAfter(response) : null;
                var errorBody = await ReadCappedAsync(response, ct).ConfigureAwait(false);
                var safeDetail = ExtractSafeErrorDetail(errorBody);
                _logger.LogWarning("Summary API returned {Status} ({Code}) detail={Detail}.",
                    (int)response.StatusCode, code, safeDetail ?? "n/a");
                throw new MeetingSummaryException(code, $"摘要服务返回 HTTP {(int)response.StatusCode}。",
                    retryAfter, safeDetail: safeDetail);
            }

            var body = await ReadCappedAsync(response, ct).ConfigureAwait(false);
            return ExtractContent(body);
        }
    }

    private Uri BuildRequestUri(Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(_transport.ApiVersion))
        {
            return baseUri;
        }
        var sep = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        return new Uri(baseUri.AbsoluteUri + sep + "api-version=" + Uri.EscapeDataString(_transport.ApiVersion));
    }

    private static string BuildBody(string model, string system, string user, int maxTokens)
    {
        // Deterministic, non-streaming; assumes only the baseline chat schema (no response_format,
        // no function calling, no streaming). System and user are separate messages.
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = 0.2,
            top_p = 0.9,
            max_tokens = maxTokens,
            stream = false
        };
        return JsonSerializer.Serialize(payload, BodyJson);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        switch (_transport.AuthenticationMode)
        {
            case TranslationAuthMode.None:
                return;
            case TranslationAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadSecret());
                return;
            case TranslationAuthMode.ApiKeyHeader:
                var header = string.IsNullOrWhiteSpace(_transport.HeaderName) ? "api-key" : _transport.HeaderName;
                request.Headers.TryAddWithoutValidation(header, ReadSecret());
                return;
        }
    }

    private string ReadSecret()
    {
        if (!_secrets.IsConfigured)
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "未配置 API Key。");
        }
        try
        {
            return _secrets.Read();
        }
        catch (Exception ex)
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "读取 API Key 失败。", inner: ex);
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } len && len > _options.MaxResponseBytes)
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidResponse, "摘要响应过大。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > _options.MaxResponseBytes)
            {
                throw new MeetingSummaryException(TranslationErrorCode.InvalidResponse, "摘要响应过大。");
            }
        }
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    private static string ExtractContent(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // A non-JSON envelope (not just non-JSON content) is treated as content to repair.
            return body;
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            throw new MeetingSummaryException(TranslationErrorCode.InvalidResponse, "摘要响应结构无效。");
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } delta)
        {
            return delta;
        }
        if (ra?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    internal static string? ExtractSafeErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return null;
            var parts = new List<string>();
            foreach (var name in new[] { "code", "type", "param" })
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var clean = new string((value.GetString() ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').Take(80).ToArray());
                    if (clean.Length > 0) parts.Add(name + "=" + clean);
                }
            }
            return parts.Count == 0 ? null : string.Join(",", parts);
        }
        catch (JsonException) { return null; }
    }
}
