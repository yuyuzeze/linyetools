using System.Net;
using System.Net.Http;
using KikuCaption.Translation.Security;

namespace KikuCaption.Summarization.Tests;

/// <summary>A scripted HttpMessageHandler — no real network. Records every request body it sends.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    public readonly List<string> RequestBodies = new();
    public readonly List<HttpRequestMessage> Requests = new();
    public int CallCount;

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public FakeHttpMessageHandler EnqueueChat(string content)
        => Enqueue(_ => Ok(ChatJson(content)));

    public FakeHttpMessageHandler EnqueueStatus(HttpStatusCode status, Action<HttpResponseMessage>? tweak = null)
        => Enqueue(_ => { var r = new HttpResponseMessage(status) { Content = new StringContent("{}") }; tweak?.Invoke(r); return r; });

    public static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static string ChatJson(string content)
        => System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        if (_responders.Count == 0)
        {
            return Ok(ChatJson("{}"));
        }
        return _responders.Dequeue()(request);
    }
}

/// <summary>An IHttpClientFactory returning one client over a fixed handler.</summary>
internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public SingleClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);
    public HttpClient CreateClient(string name) => _client;
}

/// <summary>An in-memory secret store (no DPAPI) for summary client tests.</summary>
internal sealed class FakeSecretStore : ITranslationSecretStore
{
    private string? _secret;
    public FakeSecretStore(string? secret = "test-key") => _secret = secret;
    public bool IsConfigured => _secret is not null;
    public void Save(string secret) => _secret = secret;
    public string Read() => _secret ?? throw new InvalidOperationException("not configured");
    public void Delete() => _secret = null;
    public bool HasEndpoint => false;
    public void SaveEndpoint(string endpoint) { }
    public string? ReadEndpoint() => null;
    public void DeleteEndpoint() { }
}
