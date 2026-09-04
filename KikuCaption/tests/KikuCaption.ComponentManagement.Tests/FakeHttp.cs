using System.Net;
using Microsoft.Extensions.Logging;

namespace KikuCaption.ComponentManagement.Tests;

/// <summary>A scripted HttpMessageHandler for deterministic download/manifest tests (no real network).</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
    public int RequestCount { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => _responder = responder;

    public static FakeHttpMessageHandler WithBytes(byte[] body, long? contentLength = null,
        HttpStatusCode status = HttpStatusCode.OK)
        => new((_, _) =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = contentLength ?? body.Length;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });

    public static FakeHttpMessageHandler WithString(string body, HttpStatusCode status = HttpStatusCode.OK)
        => WithBytes(System.Text.Encoding.UTF8.GetBytes(body), null, status);

    public static FakeHttpMessageHandler Throwing()
        => new((_, _) => throw new HttpRequestException("simulated network failure"));

    /// <summary>Maps each request URL to a response (redirect or final) for redirect-chain tests.</summary>
    public static FakeHttpMessageHandler ByUrl(Func<Uri, HttpResponseMessage> map)
        => new((req, _) => Task.FromResult(map(req.RequestUri!)));

    /// <summary>Builds a 3xx redirect response pointing at <paramref name="location"/>.</summary>
    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return _responder(request, cancellationToken);
    }

    public HttpClient CreateClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Wraps this fake handler as the module's controlled transport (redirect-free by nature).</summary>
    public KikuCaption.ComponentManagement.Http.IRawHttpTransport CreateTransport()
        => new KikuCaption.ComponentManagement.Http.HttpClientRawTransport(CreateClient());
}

/// <summary>Captures formatted log messages so tests can assert redaction.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}

/// <summary>A content whose declared Content-Length lies (smaller) so size-cap streaming is exercised.</summary>
internal sealed class LyingLengthContent : HttpContent
{
    private readonly byte[] _actual;
    public LyingLengthContent(byte[] actual, long declaredLength)
    {
        _actual = actual;
        Headers.ContentLength = declaredLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => stream.WriteAsync(_actual, 0, _actual.Length);

    protected override bool TryComputeLength(out long length) { length = Headers.ContentLength ?? 0; return true; }
}
