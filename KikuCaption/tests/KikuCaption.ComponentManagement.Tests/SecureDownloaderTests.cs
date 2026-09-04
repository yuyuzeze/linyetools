using System.Net;
using System.Security.Cryptography;
using KikuCaption.ComponentManagement.Downloading;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class SecureDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiku_dl", Guid.NewGuid().ToString("N"));

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private string Dest(string name = "file.zip") => Path.Combine(_dir, name);
    private static string Partial(string dest) => dest + ".partial";

    [Fact] // R7A: a valid download is written and verified; no .partial remains
    public async Task ValidDownload_Succeeds()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("hello component");
        var handler = FakeHttpMessageHandler.WithBytes(body);
        var d = new SecureDownloader(handler.CreateTransport());

        var result = await d.DownloadAsync("https://x/y.zip", Sha(body), body.Length, Dest(),
            maxBytes: 1_000_000, allowInsecureHttp: false, progress: null, CancellationToken.None);

        Assert.True(File.Exists(result.FilePath));
        Assert.False(File.Exists(Partial(Dest())));
        Assert.Equal(Sha(body), result.Sha256);
        Assert.Equal(body.Length, result.SizeBytes);
    }

    [Fact] // wrong SHA-256 → rejected, and the temp file is removed (nothing looks complete)
    public async Task WrongSha_Rejected_PartialRemoved()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var handler = FakeHttpMessageHandler.WithBytes(body);
        var d = new SecureDownloader(handler.CreateTransport());
        var wrong = new string('a', 64);

        var ex = await Assert.ThrowsAsync<DownloadValidationException>(() =>
            d.DownloadAsync("https://x/y.zip", wrong, body.Length, Dest(), 1_000_000, false, null, CancellationToken.None));

        Assert.Equal("sha256", ex.Code);
        Assert.False(File.Exists(Dest()));
        Assert.False(File.Exists(Partial(Dest())));
    }

    [Fact] // exceeding the size cap mid-stream → rejected + cleaned up
    public async Task OverMaxSize_Rejected()
    {
        var body = new byte[5000];
        var handler = FakeHttpMessageHandler.WithBytes(body);
        var d = new SecureDownloader(handler.CreateTransport());

        var ex = await Assert.ThrowsAsync<DownloadValidationException>(() =>
            d.DownloadAsync("https://x/y.zip", Sha(body), null, Dest(), maxBytes: 1024, allowInsecureHttp: false,
                progress: null, CancellationToken.None));

        Assert.Equal("size", ex.Code);
        Assert.False(File.Exists(Dest()));
        Assert.False(File.Exists(Partial(Dest())));
    }

    [Fact] // a Content-Length that disagrees with the manifest size → rejected
    public async Task ContentLengthMismatch_Rejected()
    {
        var body = new byte[100];
        var handler = FakeHttpMessageHandler.WithBytes(body, contentLength: 100);
        var d = new SecureDownloader(handler.CreateTransport());

        var ex = await Assert.ThrowsAsync<DownloadValidationException>(() =>
            d.DownloadAsync("https://x/y.zip", Sha(body), expectedSizeBytes: 999, Dest(), 1_000_000, false, null, CancellationToken.None));

        Assert.Equal("length", ex.Code);
        Assert.False(File.Exists(Dest()));
    }

    [Fact] // a non-HTTPS URL is rejected before any request
    public async Task NonHttps_Rejected()
    {
        var handler = FakeHttpMessageHandler.WithBytes(new byte[1]);
        var d = new SecureDownloader(handler.CreateTransport());

        var ex = await Assert.ThrowsAsync<DownloadValidationException>(() =>
            d.DownloadAsync("http://x/y.zip", new string('0', 64), null, Dest(), 1_000_000, false, null, CancellationToken.None));
        Assert.Equal("url", ex.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact] // R7A: cancellation mid-stream leaves NO file that could be mistaken for complete
    public async Task Cancellation_LeavesNoFile()
    {
        // The stream yields one chunk then throws OperationCanceledException on the next read.
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new ThrowAfterFirstReadStream()) }));
        var d = new SecureDownloader(handler.CreateTransport());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            d.DownloadAsync("https://x/y.zip", new string('0', 64), null, Dest(), 1_000_000, false, null, CancellationToken.None));

        Assert.False(File.Exists(Dest()));
        Assert.False(File.Exists(Partial(Dest())));
    }

    [Fact] // an already-cached, hash-matching file is reused without a new request or overwrite
    public async Task CacheHit_NoReDownload()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("cached");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Dest(), body);
        var handler = FakeHttpMessageHandler.WithBytes(body);
        var d = new SecureDownloader(handler.CreateTransport());

        var result = await d.DownloadAsync("https://x/y.zip", Sha(body), body.Length, Dest(), 1_000_000, false, null, CancellationToken.None);

        Assert.Equal(Dest(), result.FilePath);
        Assert.Equal(0, handler.RequestCount); // reused the verified cache; no network
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Yields 512 bytes once, then throws OperationCanceledException — simulates mid-download cancel.</summary>
    private sealed class ThrowAfterFirstReadStream : Stream
    {
        private bool _first = true;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_first) { _first = false; return Math.Min(512, count); }
            throw new OperationCanceledException();
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first) { _first = false; return ValueTask.FromResult(Math.Min(512, buffer.Length)); }
            throw new OperationCanceledException();
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
