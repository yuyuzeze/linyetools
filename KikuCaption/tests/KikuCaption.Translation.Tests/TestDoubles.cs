using System.Net;
using System.Net.Http;
using KikuCaption.Translation.Security;

namespace KikuCaption.Translation.Tests;

/// <summary>An HttpMessageHandler driven by a caller-supplied delegate — no real network is used.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public int CallCount;
    public HttpRequestMessage? LastRequest;
    public string? LastRequestBody;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responder = responder;

    public static FakeHttpMessageHandler Json(HttpStatusCode status, string body,
        Action<HttpResponseMessage>? tweak = null)
        => new((_, _) =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
            tweak?.Invoke(resp);
            return Task.FromResult(resp);
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CallCount);
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await _responder(request, cancellationToken);
    }
}

/// <summary>An IHttpClientFactory that returns ONE reusable client over a fixed handler.</summary>
internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public int CreateCount;

    public SingleClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);

    public HttpClient CreateClient(string name)
    {
        Interlocked.Increment(ref CreateCount);
        return _client;
    }
}

/// <summary>A simple in-memory secret store for adapter tests (no DPAPI/registry).</summary>
internal sealed class FakeSecretStore : ITranslationSecretStore
{
    private string? _secret;
    private string? _endpoint;

    public FakeSecretStore(string? secret = "test-secret") => _secret = secret;

    public bool IsConfigured => _secret is not null;
    public void Save(string secret) => _secret = secret;
    public string Read() => _secret ?? throw new InvalidOperationException("not configured");
    public void Delete() => _secret = null;

    public bool HasEndpoint => _endpoint is not null;
    public void SaveEndpoint(string endpoint) => _endpoint = endpoint;
    public string? ReadEndpoint() => _endpoint;
    public void DeleteEndpoint() => _endpoint = null;
}

/// <summary>A translator stub that records calls and returns/throws per script (queue tests).</summary>
internal sealed class ScriptedTranslator : KikuCaption.Core.Interfaces.IAiTranslationService
{
    private readonly Func<string, CancellationToken, Task<string>> _behavior;
    public int Calls;
    public readonly List<string> Inputs = new();

    public ScriptedTranslator(Func<string, CancellationToken, Task<string>> behavior) => _behavior = behavior;

    public async Task<string> TranslateAsync(KikuCaption.Core.Models.TranslationRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        lock (Inputs) { Inputs.Add(request.Text); }
        return await _behavior(request.Text, cancellationToken);
    }
}
