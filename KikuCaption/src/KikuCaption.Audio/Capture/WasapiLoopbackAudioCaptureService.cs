using System.Runtime.CompilerServices;
using KikuCaption.Audio.Buffering;
using KikuCaption.Audio.Conversion;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KikuCaption.Audio.Capture;

/// <summary>
/// Captures Windows system output ("what you hear") via WASAPI loopback and yields it as a
/// stream of <see cref="AudioChunk"/> normalized to 16 kHz / mono / int16 (PROJECT.md 5.2, 8.2).
///
/// Capture runs on NAudio's own thread; converted chunks are handed off through a bounded
/// buffer, so the UI thread is never involved and memory is bounded. A single instance
/// captures at most once (the WAV recorder creates a fresh instance per session).
/// </summary>
public sealed class WasapiLoopbackAudioCaptureService : IAudioCaptureService
{
    private const int StateIdle = 0;
    private const int StateCapturing = 1;
    private const int StateStopped = 2;
    private const int StateDisposed = 3;

    // ~256 chunks (~2.5 s at 10 ms/chunk) of head-room before back-pressure drops kick in.
    private const int BufferCapacity = 256;

    private readonly ILogger<WasapiLoopbackAudioCaptureService> _logger;
    private int _state = StateIdle;

    public WasapiLoopbackAudioCaptureService(ILogger<WasapiLoopbackAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateCapturing, StateIdle);
        if (previous == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(WasapiLoopbackAudioCaptureService));
        }

        if (previous != StateIdle)
        {
            throw new InvalidOperationException("捕获已开始或已结束；每个实例只能捕获一次。");
        }

        return CaptureCore(cancellationToken);
    }

    private async IAsyncEnumerable<AudioChunk> CaptureCore(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new BoundedAudioBuffer(BufferCapacity);
        WasapiLoopbackCapture? capture = null;

        try
        {
            try
            {
                capture = new WasapiLoopbackCapture();
            }
            catch (Exception ex)
            {
                throw new AudioCaptureException(
                    "无法初始化系统音频捕获设备（可能没有可用的输出设备）。", ex);
            }

            var converter = new AudioFormatConverter(capture.WaveFormat);
            long producedSamples = 0;

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                byte[] pcm;
                try
                {
                    pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
                }
                catch (Exception ex)
                {
                    buffer.Complete(new AudioCaptureException("音频格式转换失败。", ex));
                    return;
                }

                if (pcm.Length < 2)
                {
                    return;
                }

                int sampleCount = pcm.Length / 2;
                var timestamp = TimeSpan.FromSeconds((double)producedSamples / AudioFormatConverter.TargetSampleRate);
                var duration = TimeSpan.FromSeconds((double)sampleCount / AudioFormatConverter.TargetSampleRate);
                producedSamples += sampleCount;

                buffer.TryWrite(new AudioChunk(pcm, timestamp, duration));
            };

            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    buffer.Complete(new AudioCaptureException("系统音频设备中断或捕获过程失败。", e.Exception));
                }
                else
                {
                    buffer.Complete();
                }
            };

            using var registration = cancellationToken.Register(() =>
            {
                try { capture!.StopRecording(); } catch { /* best effort */ }
            });

            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                throw new AudioCaptureException("启动系统音频捕获失败。", ex);
            }

            _logger.LogInformation(
                "System audio capture started (source {SampleRate} Hz, {Channels}ch, {Bits}-bit).",
                capture.WaveFormat.SampleRate, capture.WaveFormat.Channels, capture.WaveFormat.BitsPerSample);

            await foreach (var chunk in buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            if (capture is not null)
            {
                try { capture.StopRecording(); } catch { /* best effort */ }
                capture.Dispose();
            }

            Interlocked.CompareExchange(ref _state, StateStopped, StateCapturing);
            _logger.LogInformation(
                "System audio capture stopped. Dropped chunks (back-pressure): {Dropped}.",
                buffer.DroppedChunkCount);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _state, StateDisposed);
        return ValueTask.CompletedTask;
    }
}
