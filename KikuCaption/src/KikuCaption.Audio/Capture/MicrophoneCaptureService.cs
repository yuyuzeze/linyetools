using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
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
/// Captures a Windows microphone (input) endpoint via WASAPI and yields it as a stream of
/// <see cref="AudioChunk"/> normalized to 16 kHz / mono / int16 — the same recognition format the
/// loopback capture produces, so the mixer can sum the two directly (UI-R5A).
///
/// The device is chosen by a stable endpoint id; a null/blank/unknown id falls back to the default
/// <b>communications</b> input device. If no usable device exists the stream throws
/// <see cref="AudioCaptureException"/> — a missing/failed microphone is never silently ignored.
/// Capture runs on NAudio's own thread and hands chunks off through a bounded buffer, so the UI/WASAPI
/// threads are never blocked and memory stays bounded. A single instance captures at most once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneCaptureService : IAudioCaptureService
{
    private const int StateIdle = 0;
    private const int StateCapturing = 1;
    private const int StateStopped = 2;
    private const int StateDisposed = 3;

    private const int BufferCapacity = 256; // ~2.5 s head-room before back-pressure drops kick in

    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private int _state = StateIdle;

    public MicrophoneCaptureService(string? deviceId, ILogger logger)
    {
        _deviceId = deviceId;
        _logger = logger;
    }

    public IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateCapturing, StateIdle);
        if (previous == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(MicrophoneCaptureService));
        }

        if (previous != StateIdle)
        {
            throw new InvalidOperationException("麦克风捕获已开始或已结束；每个实例只能捕获一次。");
        }

        return CaptureCore(cancellationToken);
    }

    private async IAsyncEnumerable<AudioChunk> CaptureCore(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new BoundedAudioBuffer(BufferCapacity);
        MMDevice? device = null;
        WasapiCapture? capture = null;

        try
        {
            try
            {
                device = ResolveDevice(_deviceId);
                capture = new WasapiCapture(device);
            }
            catch (AudioCaptureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AudioCaptureException("无法初始化麦克风捕获设备（设备可能已拔出或被占用）。", ex);
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
                    buffer.Complete(new AudioCaptureException("麦克风音频格式转换失败。", ex));
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
                    buffer.Complete(new AudioCaptureException("麦克风设备中断或捕获过程失败。", e.Exception));
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
                throw new AudioCaptureException("启动麦克风捕获失败。", ex);
            }

            // Log only non-sensitive device facts (never PCM). Device name is a hardware label.
            _logger.LogInformation(
                "Microphone capture started (source {SampleRate} Hz, {Channels}ch, {Bits}-bit).",
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

            device?.Dispose();
            Interlocked.CompareExchange(ref _state, StateStopped, StateCapturing);
            _logger.LogInformation(
                "Microphone capture stopped. Dropped chunks (back-pressure): {Dropped}.",
                buffer.DroppedChunkCount);
        }
    }

    /// <summary>
    /// Resolves the capture endpoint: the requested stable id if it is active, otherwise the default
    /// communications input device. Throws if there is no usable input device at all.
    /// </summary>
    private static MMDevice ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (string.Equals(d.ID, deviceId, StringComparison.Ordinal))
                {
                    return d;
                }

                d.Dispose();
            }
            // Saved device is gone → fall through to the default (safe fallback, never silent crash).
        }

        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        throw new AudioCaptureException("未找到可用的麦克风输入设备。");
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _state, StateDisposed);
        return ValueTask.CompletedTask;
    }
}
