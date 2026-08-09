using System.Diagnostics;
using System.Runtime.CompilerServices;
using KikuCaption.Audio.Capture;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Xunit;

namespace KikuCaption.Audio.Tests;

public class SystemAudioWavRecorderTests
{
    // A capture source that emits a fixed number of chunks, then blocks until cancelled
    // (emulating a live device kept alive until Stop is called).
    private sealed class FakeCaptureService : IAudioCaptureService
    {
        private readonly int _chunkCount;
        private readonly int _chunkBytes;
        private readonly int _delayMs;

        public FakeCaptureService(int chunkCount, int chunkBytes = 3200, int delayMs = 10)
        {
            _chunkCount = chunkCount;
            _chunkBytes = chunkBytes;
            _delayMs = delayMs;
        }

        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
        {
            for (int i = 0; i < _chunkCount; i++)
            {
                await Task.Delay(_delayMs, ct).ConfigureAwait(false);
                yield return new AudioChunk(new byte[_chunkBytes],
                    TimeSpan.FromMilliseconds(i * 100), TimeSpan.FromMilliseconds(100));
            }

            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultingCaptureService : IAudioCaptureService
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
            yield return new AudioChunk(new byte[3200], TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            await Task.Delay(10, ct).ConfigureAwait(false);
            throw new AudioCaptureException("simulated device loss");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static SystemAudioWavRecorder CreateRecorder(IAudioCaptureService service) =>
        new(() => service, NullLogger<SystemAudioWavRecorder>.Instance);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }
    }

    private static string TempWavPath() =>
        Path.Combine(Path.GetTempPath(), $"kiku_rec_{Guid.NewGuid():N}.wav");

    [Fact]
    public async Task StartThenStop_ProducesWav_AndDisposesService()
    {
        var fake = new FakeCaptureService(chunkCount: 5);
        var recorder = CreateRecorder(fake);
        var path = TempWavPath();

        try
        {
            await recorder.StartAsync(path);
            Assert.Equal(AudioRecorderState.Capturing, recorder.State);

            await WaitForAsync(() => recorder.BytesWritten > 0);
            await recorder.StopAsync();

            Assert.Equal(AudioRecorderState.Stopped, recorder.State);
            Assert.True(fake.Disposed);
            Assert.True(recorder.BytesWritten > 0);

            using var reader = new WaveFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Start_WhenAlreadyCapturing_Throws()
    {
        var recorder = CreateRecorder(new FakeCaptureService(chunkCount: 100));
        var path1 = TempWavPath();
        var path2 = TempWavPath();

        try
        {
            await recorder.StartAsync(path1);
            // StartAsync validates and throws synchronously (before returning the Task).
            Assert.Throws<InvalidOperationException>(() => { _ = recorder.StartAsync(path2); });
        }
        finally
        {
            await recorder.StopAsync();
            if (File.Exists(path1)) File.Delete(path1);
            if (File.Exists(path2)) File.Delete(path2);
        }
    }

    [Fact]
    public async Task Stop_WhenNotCapturing_IsNoOp()
    {
        var recorder = CreateRecorder(new FakeCaptureService(chunkCount: 1));

        await recorder.StopAsync(); // never started
        Assert.Equal(AudioRecorderState.Idle, recorder.State);

        var path = TempWavPath();
        try
        {
            await recorder.StartAsync(path);
            await recorder.StopAsync();
            await recorder.StopAsync(); // double stop is safe
            Assert.Equal(AudioRecorderState.Stopped, recorder.State);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancellation_StopsCapture()
    {
        var recorder = CreateRecorder(new FakeCaptureService(chunkCount: 1000));
        using var cts = new CancellationTokenSource();
        var path = TempWavPath();

        try
        {
            await recorder.StartAsync(path, cts.Token);
            await WaitForAsync(() => recorder.BytesWritten > 0);
            cts.Cancel();

            await WaitForAsync(() => recorder.State == AudioRecorderState.Stopped);
            Assert.Equal(AudioRecorderState.Stopped, recorder.State);
        }
        finally
        {
            await recorder.StopAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Start_RefusesToOverwriteExistingFile()
    {
        var recorder = CreateRecorder(new FakeCaptureService(chunkCount: 1));
        var path = TempWavPath();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        try
        {
            await Assert.ThrowsAsync<IOException>(() => recorder.StartAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Fault_SetsFaultedState_RaisesEvent_AndKeepsWrittenData()
    {
        var fake = new FaultingCaptureService();
        var recorder = CreateRecorder(fake);
        var path = TempWavPath();
        var faultRaised = false;
        recorder.Faulted += (_, _) => faultRaised = true;

        try
        {
            await recorder.StartAsync(path);
            await WaitForAsync(() => recorder.State == AudioRecorderState.Faulted);

            Assert.Equal(AudioRecorderState.Faulted, recorder.State);
            Assert.True(faultRaised);
            Assert.True(fake.Disposed);
            Assert.True(File.Exists(path)); // partial data preserved

            using var reader = new WaveFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
