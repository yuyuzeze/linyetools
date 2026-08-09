using KikuCaption.Audio.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Audio.Tests;

/// <summary>
/// State-machine guard tests for the real capture service. These do not enumerate the
/// stream, so no audio device is required (the guards run before any WASAPI object is created).
/// </summary>
public class WasapiLoopbackAudioCaptureServiceTests
{
    private static WasapiLoopbackAudioCaptureService Create() =>
        new(NullLogger<WasapiLoopbackAudioCaptureService>.Instance);

    [Fact]
    public async Task CaptureAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var service = Create();
        await service.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => service.CaptureAsync(CancellationToken.None));
    }

    [Fact]
    public void CaptureAsync_CalledTwice_ThrowsInvalidOperation()
    {
        var service = Create();

        _ = service.CaptureAsync(CancellationToken.None); // transitions to Capturing (lazy, no device)

        Assert.Throws<InvalidOperationException>(() => service.CaptureAsync(CancellationToken.None));
    }
}
