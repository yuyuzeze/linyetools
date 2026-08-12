using System.Diagnostics;
using KikuCaption.Audio.Buffering;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Audio.Mixing;

/// <summary>Which inputs a meeting captures. Not secret; safe to persist / log by flag.</summary>
public sealed record AudioMixOptions(bool RecordSystemAudio, bool RecordMicrophone, string? MicrophoneDeviceId)
{
    public bool AnyInput => RecordSystemAudio || RecordMicrophone;
}

/// <summary>
/// Session-scoped audio mixer (UI-R5A). Opens at most ONE system-loopback capture and at most ONE
/// microphone capture, sums them on a single monotonic clock via <see cref="AudioMixTimeline"/>, and
/// fans the resulting 16 kHz/mono/int16 mixed PCM out to two independent, bounded consumers — the
/// real-time caption pipeline and the FFmpeg recorder — so exactly one WASAPI loopback exists and
/// both consumers hear the same mix (system + microphone).
///
/// Fan-out is back-pressure isolated: each consumer has its own bounded buffer, and a slow consumer
/// only drops its own branch (counted) — it can never block the mix loop, the WASAPI threads, or the
/// other consumer, and neither branch can grow without bound. A microphone failure is non-fatal (mic
/// falls to silence, system audio and recording continue); a system-capture failure faults the
/// branches (matching the pre-R5A loopback-only behavior).
/// </summary>
public sealed class SessionAudioMixer : IAsyncDisposable
{
    private const int BranchCapacity = 256; // ~5 s of 20 ms mixed chunks before a branch drops

    private readonly IAudioCaptureService? _system;
    private readonly IAudioCaptureService? _mic;
    private readonly ILogger _logger;
    private readonly int _frameMs;
    private readonly AudioMixTimeline _timeline;

    private readonly BoundedAudioBuffer _speechBranch = new(BranchCapacity);
    private readonly BranchSource _speechSource;
    private BoundedAudioBuffer? _recordingBranch;
    private BranchSource? _recordingSource;

    private CancellationTokenSource? _cts;
    private Task? _systemPump;
    private Task? _micPump;
    private Task? _mixLoop;
    private Stopwatch? _clock;
    private long _speechDropped;
    private long _recordingDropped;
    private int _started;
    private int _stopped;

    public SessionAudioMixer(IAudioCaptureService? systemSource, IAudioCaptureService? micSource, ILogger logger, int frameMilliseconds = 20)
    {
        if (systemSource is null && micSource is null)
        {
            throw new ArgumentException("至少需要一个音频输入（系统声音或麦克风）。");
        }

        _system = systemSource;
        _mic = micSource;
        _logger = logger;
        _frameMs = frameMilliseconds;
        _timeline = new AudioMixTimeline(frameMilliseconds);
        _speechSource = new BranchSource(_speechBranch);
    }

    /// <summary>The mixed-audio source for the caption pipeline (always available; single reader).</summary>
    public IAudioCaptureService SpeechSource => _speechSource;

    public AudioMixMetrics GetMetrics() => _timeline.GetMetrics(_clock?.Elapsed ?? TimeSpan.Zero);

    public long SpeechDroppedChunks => Interlocked.Read(ref _speechDropped);
    public long RecordingDroppedChunks => Interlocked.Read(ref _recordingDropped);

    /// <summary>
    /// Enables a second fan-out branch for the recorder and returns it as an audio source. Call before
    /// <see cref="Start"/>. The recorder consumes mixed PCM identical to the caption pipeline's.
    /// </summary>
    public IAudioCaptureService CreateRecordingSource()
    {
        _recordingBranch ??= new BoundedAudioBuffer(BranchCapacity);
        return _recordingSource ??= new BranchSource(_recordingBranch);
    }

    /// <summary>Starts the capture pumps and the mix loop. Idempotent-safe (second call is a no-op).</summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        if (_system is not null)
        {
            _systemPump = Task.Run(() => PumpAsync(_system, isSystem: true, token));
        }
        if (_mic is not null)
        {
            _micPump = Task.Run(() => PumpAsync(_mic, isSystem: false, token));
        }

        _clock = Stopwatch.StartNew();
        _mixLoop = Task.Run(() => MixLoopAsync(token));
    }

    private async Task PumpAsync(IAudioCaptureService source, bool isSystem, CancellationToken token)
    {
        try
        {
            await foreach (var chunk in source.CaptureAsync(token).ConfigureAwait(false))
            {
                if (isSystem) { _timeline.AppendSystem(chunk.Pcm.Span); }
                else { _timeline.AppendMic(chunk.Pcm.Span); }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            if (isSystem)
            {
                // System capture is the primary source: propagate the fault to the consumers, matching
                // the pre-R5A loopback-only behavior (captions fault on a system-audio device error).
                _logger.LogWarning(ex, "System audio capture ended unexpectedly; faulting mixed output.");
                _speechBranch.Complete(ex);
                _recordingBranch?.Complete(ex);
            }
            else
            {
                // A microphone failure is non-fatal: the mic simply falls to silence and the session
                // (system audio + recording + captions) continues.
                _logger.LogWarning(ex, "Microphone capture ended unexpectedly; continuing without the microphone.");
            }
        }
    }

    // Emits mixed PCM due per the monotonic clock (real mix when available, digital silence otherwise)
    // to both branches. Never blocks: a full branch drops its own chunk (counted).
    private async Task MixLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                EmitDue();
                await Task.Delay(_frameMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio mix loop error.");
        }
    }

    private void EmitDue()
    {
        var pcm = _timeline.ProduceUpTo(_clock!.Elapsed);
        if (pcm.Length == 0)
        {
            return;
        }

        int samples = pcm.Length / 2;
        var chunk = new AudioChunk(pcm, TimeSpan.Zero, TimeSpan.FromSeconds((double)samples / AudioMixTimeline.SampleRate));

        if (!_speechBranch.TryWrite(chunk)) { Interlocked.Increment(ref _speechDropped); }
        if (_recordingBranch is not null && !_recordingBranch.TryWrite(chunk)) { Interlocked.Increment(ref _recordingDropped); }
    }

    /// <summary>
    /// Stops capture and the mix loop, flushes the final mixed tail to both branches, then completes
    /// them so the consumers finish enumerating. Idempotent. The caller must have already drained the
    /// consumers up to this point; the tail delivers the last mixed PCM (final mic/system audio).
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0)
        {
            return;
        }

        var endElapsed = _clock?.Elapsed ?? TimeSpan.Zero;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_systemPump is not null) { try { await _systemPump.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_micPump is not null) { try { await _micPump.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_mixLoop is not null) { try { await _mixLoop.ConfigureAwait(false); } catch { /* ignore */ } }

        // Flush the final mixed tail (last captured mic/system PCM) so no audio is silently dropped.
        try
        {
            var tail = _timeline.Flush(endElapsed);
            if (tail.Length > 0)
            {
                int samples = tail.Length / 2;
                var chunk = new AudioChunk(tail, TimeSpan.Zero, TimeSpan.FromSeconds((double)samples / AudioMixTimeline.SampleRate));
                _speechBranch.TryWrite(chunk);
                _recordingBranch?.TryWrite(chunk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing the final mixed audio tail.");
        }

        _speechBranch.Complete();
        _recordingBranch?.Complete();

        if (_system is not null) { try { await _system.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ } }
        if (_mic is not null) { try { await _mic.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ } }

        var m = GetMetrics();
        _logger.LogInformation(
            "Audio mixer stopped. mixed={Mixed}s sysReal={SysReal}s micReal={MicReal}s clipped={Clipped} " +
            "speechDropped={SpeechDrop} recDropped={RecDrop}.",
            m.MixedSamples / (double)AudioMixTimeline.SampleRate,
            m.SystemRealSamples / (double)AudioMixTimeline.SampleRate,
            m.MicRealSamples / (double)AudioMixTimeline.SampleRate,
            m.ClippedSamples, SpeechDroppedChunks, RecordingDroppedChunks);

        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Adapts one bounded fan-out branch as an <see cref="IAudioCaptureService"/> for a consumer.</summary>
    private sealed class BranchSource : IAudioCaptureService
    {
        private readonly BoundedAudioBuffer _buffer;
        public BranchSource(BoundedAudioBuffer buffer) => _buffer = buffer;
        public IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken) => _buffer.ReadAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
