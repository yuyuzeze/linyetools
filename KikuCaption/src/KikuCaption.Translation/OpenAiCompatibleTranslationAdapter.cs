using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Translation.Security;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Translation;

/// <summary>
/// Standard OpenAI-compatible Chat Completions adapter (M6 §1). All wire-protocol specifics are
/// isolated here: the request URL, auth header, system/user split, low-randomness parameters, HTTPS
/// enforcement, size/length limits, JSON validation, and error classification. No Microsoft domain,
/// deployment URL, or API version is hard-coded — <see cref="TranslationOptions.Endpoint"/> is the
/// full address and <c>api-version</c> is only appended when configured.
///
/// <para>The API key is read from the DPAPI secret store per request and never logged.</para>
/// </summary>
public sealed class OpenAiCompatibleTranslationAdapter : IAiTranslationService
{
    public const string HttpClientName = "translation";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITranslationSecretStore _secrets;
    private readonly TranslationOptions _options;
    private readonly ILogger<OpenAiCompatibleTranslationAdapter> _logger;

    public OpenAiCompatibleTranslationAdapter(
        IHttpClientFactory httpClientFactory,
        ITranslationSecretStore secrets,
        TranslationOptions options,
        ILogger<OpenAiCompatibleTranslationAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _options = options;
        _logger = logger;
    }

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || !Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var baseUri))
        {
            throw new TranslationException(TranslationErrorCode.InvalidConfig, "翻译 Endpoint 未配置或无效。");
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new TranslationException(TranslationErrorCode.InvalidConfig, "翻译 Endpoint 必须使用 HTTPS。");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TranslationException(TranslationErrorCode.InvalidResponse, "空文本不翻译。");
        }

        if (text.Length > _options.MaxInputCharacters)
        {
            throw new TranslationException(TranslationErrorCode.InputTooLong,
                $"字幕长度 {text.Length} 超过上限 {_options.MaxInputCharacters}，拒绝发送。");
        }

        var requestUri = BuildRequestUri(baseUri);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(BuildRequestBody(text), Encoding.UTF8, "application/json")
        };
        ApplyAuth(request);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TranslationException(TranslationErrorCode.Cancelled, "翻译已取消。");
        }
        catch (OperationCanceledException)
        {
            throw new TranslationException(TranslationErrorCode.Timeout, "翻译请求超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(TranslationErrorCode.Network, "网络错误。", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = TranslationErrorClassifier.FromStatus(response.StatusCode);
                TimeSpan? retryAfter = code == TranslationErrorCode.RateLimited ? ParseRetryAfter(response) : null;
                _logger.LogWarning("Translation API returned {Status} ({Code}).", (int)response.StatusCode, code);
                throw new TranslationException(code, $"翻译服务返回 HTTP {(int)response.StatusCode}。", retryAfter);
            }

            EnsureBodyWithinLimit(response);
            var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            return ExtractTranslation(body);
        }
    }

    private Uri BuildRequestUri(Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiVersion))
        {
            return baseUri;
        }

        // Append api-version without assuming a Microsoft URL shape.
        var separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        return new Uri(baseUri.AbsoluteUri + separator + "api-version=" + Uri.EscapeDataString(_options.ApiVersion));
    }

    private string BuildRequestBody(string text)
    {
        // Low randomness for faithful translation; original text is a SEPARATE user message.
        int maxTokens = Math.Min(2048, (text.Length * 2) + 64);
        var payload = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = TranslationPrompt.System },
                new { role = "user", content = text }
            },
            temperature = 0.2,
            top_p = 0.9,
            max_tokens = maxTokens,
            stream = false
        };

        return JsonSerializer.Serialize(payload);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        switch (_options.AuthenticationMode)
        {
            case TranslationAuthMode.None:
                return;

            case TranslationAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadSecret());
                return;

            case TranslationAuthMode.ApiKeyHeader:
                var header = string.IsNullOrWhiteSpace(_options.HeaderName) ? "api-key" : _options.HeaderName;
                request.Headers.TryAddWithoutValidation(header, ReadSecret());
                return;
        }
    }

    private string ReadSecret()
    {
        if (!_secrets.IsConfigured)
        {
            throw new TranslationException(TranslationErrorCode.InvalidConfig, "未配置 API Key。");
        }

        try
        {
            return _secrets.Read();
        }
        catch (Exception ex)
        {
            throw new TranslationException(TranslationErrorCode.InvalidConfig, "读取 API Key 失败（密文可能损坏）。", inner: ex);
        }
    }

    private void EnsureBodyWithinLimit(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is { } len && len > _options.MaxResponseBytes)
        {
            throw new TranslationException(TranslationErrorCode.InvalidResponse, "翻译响应过大。");
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > _options.MaxResponseBytes)
            {
                throw new TranslationException(TranslationErrorCode.InvalidResponse, "翻译响应过大。");
            }
        }

        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    private static string ExtractTranslation(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new TranslationException(TranslationErrorCode.InvalidResponse, "翻译响应不是有效 JSON。");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                throw new TranslationException(TranslationErrorCode.InvalidResponse, "翻译响应结构无效。");
            }

            var result = content.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new TranslationException(TranslationErrorCode.InvalidResponse, "翻译结果为空。");
            }

            return result;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
        {
            return null;
        }

        if (ra.Delta is { } delta)
        {
            return delta;
        }

        if (ra.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
