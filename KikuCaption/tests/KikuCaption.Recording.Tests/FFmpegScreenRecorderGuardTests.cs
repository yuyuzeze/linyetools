using System.Runtime.CompilerServices;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Recording.Tests;

public class FFmpegScreenRecorderGuardTests
{
    private sealed class NoopAudioCaptureService : IAudioCaptureService
    {
        public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FFmpegScreenRecorder Create()
        => new(() => new NoopAudioCaptureService(), NullLogger<FFmpegScreenRecorder>.Instance);

    [Fact]
    public async Task Start_MissingFFmpeg_Throws_AndFaults()
    {
        await using var recorder = Create();
        var options = new RecordingOptions
        {
            CaptureType = CaptureTargetType.Screen,
            OutputPath = Path.Combine(Path.GetTempPath(), "kiku_x.mp4"),
            FFmpegPath = @"C:\definitely\missing\ffmpeg.exe"
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => recorder.StartAsync(options, CancellationToken.None));
        Assert.Equal("ffmpeg_missing", ex.Code);
        Assert.Equal(RecorderState.Faulted, recorder.State);

        // Repeated start after fault is rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StartAsync(options, CancellationToken.None));
    }

    [Fact]
    public async Task Start_WindowWithoutTitle_Throws()
    {
        var stubFfmpeg = Path.Combine(Directory.CreateTempSubdirectory("kiku_stub").FullName, "ffmpeg.exe");
        File.WriteAllText(stubFfmpeg, "stub"); // exists → passes the missing-check; window check throws next
        await using var recorder = Create();
        var options = new RecordingOptions
        {
            CaptureType = CaptureTargetType.Window,
            TargetTitle = null,
            OutputPath = Path.Combine(Path.GetTempPath(), "kiku_x.mp4"),
            FFmpegPath = stubFfmpeg
        };

        var ex = await Assert.ThrowsAsync<RecordingException>(() => recorder.StartAsync(options, CancellationToken.None));
        Assert.Equal("target_missing", ex.Code);
    }

    [Fact]
    public async Task Stop_WhenIdle_Throws()
    {
        await using var recorder = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.StopAsync(CancellationToken.None));
    }
}
