using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Recording.CaptureTargets;
using KikuCaption.Recording.FFmpeg;
using KikuCaption.Recording.Muxing;
using KikuCaption.Recording.Processes;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Recording;

/// <summary>
/// <see cref="IScreenRecorder"/> backed by a managed FFmpeg subprocess: gdigrab video + WASAPI
/// loopback audio (via a named pipe) → H.264/AAC MP4. Structured args (no shell); graceful stop
/// via stdin 'q' then kill-tree timeout; Job Object prevents orphan FFmpeg; validates the output
/// with ffprobe and never reports a broken file as complete (PROJECT.md 5.3, 8.2, 16).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FFmpegScreenRecorder : IScreenRecorder
{
    private const int StateIdle = 0;
    private const int StateStarting = 1;
    private const int StateRecording = 2;
    private const int StateStopping = 3;
    private const int StateStopped = 4;
    private const int StateFaulted = 5;

    private readonly Func<IAudioCaptureService> _audioFactory;
    private readonly ILogger<FFmpegScreenRecorder> _logger;
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrGate = new();

    private int _state = StateIdle;
    private RecordingOptions? _options;
    private string? _ffprobePath;
    private Process? _process;
    private NamedPipeAudioSink? _sink;
    private IAudioCaptureService? _audio;
    private AudioTimeline? _timeline;
    private Task? _audioPump;
    private Task? _outputLoop;
    private Stopwatch? _clock;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _pumpCts;
    private WindowsJobObject? _job;
    private RecordingResult? _result;
    private volatile bool _audioTimelineFaulted;

    public FFmpegScreenRecorder(Func<IAudioCaptureService> audioFactory, ILogger<FFmpegScreenRecorder> logger)
    {
        _audioFactory = audioFactory;
        _logger = logger;
    }

    public RecorderState State => (RecorderState)Volatile.Read(ref _state);

    public int? RecordingProcessId
    {
        get
        {
            var p = _process;
            try { return p is { HasExited: false } ? p.Id : null; }
            catch { return null; }
        }
    }

    public AudioTimelineMetrics? AudioMetrics => _timeline?.GetMetrics(_clock?.Elapsed ?? TimeSpan.Zero);

    /// <summary>Late/overflowed audio samples dropped by the bounded jitter buffer.</summary>
    public long DroppedAudioChunks => AudioMetrics?.DroppedLateSamples ?? 0;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, StateStarting, StateIdle) != StateIdle)
        {
            throw new InvalidOperationException("录制已在进行或已结束。");
        }

        try
        {
            if (!File.Exists(options.FFmpegPath))
            {
                throw new RecordingException("ffmpeg_missing", $"未找到 FFmpeg：{options.FFmpegPath}");
            }

            if (options.CaptureType == CaptureTargetType.Window &&
                (string.IsNullOrWhiteSpace(options.TargetTitle) || !WindowEnumerator.WindowExists(options.TargetTitle!)))
            {
                throw new RecordingException("target_missing", "目标窗口不存在或已关闭。");
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            _options = options;
            _ffprobePath = FFmpegLocator.LocateFFprobe(options.FFmpegPath);

            string? pipeName = null;
            if (options.IncludeSystemAudio)
            {
                _sink = new NamedPipeAudioSink(_logger);
                _sink.CreateServer();
                pipeName = _sink.PipeName;
                _timeline = new AudioTimeline();

                // Warm up audio capture BEFORE FFmpeg (feeding the timeline's jitter buffer) so its
                // cold-start latency is over by the recording epoch — real audio then flows immediately.
                // UI-R5A: prefer the externally-supplied mixed source (system + mic from the session
                // mixer) when provided; otherwise open a loopback ourselves (legacy behavior).
                _cts = new CancellationTokenSource();
                _pumpCts = new CancellationTokenSource();
                _audio = options.ExternalAudioSource ?? _audioFactory();
                _audioPump = Task.Run(() => AudioPumpAsync(_pumpCts.Token));
            }

            var args = FFmpegArgumentBuilder.Build(options, pipeName);
            StartProcess(options.FFmpegPath, args);

            if (options.IncludeSystemAudio && _sink is not null && _timeline is not null)
            {
                // Recording epoch = FFmpeg launch (≈ first video frame, start_time 0). Drop the
                // warm-up backlog and start the clock now. Audio captured during FFmpeg's input
                // initialization is buffered and back-filled into the timeline once the pipe
                // connects, so the audio covers the same span as the video (no leading clip).
                _timeline.Reset();
                _clock = Stopwatch.StartNew();
                await _sink.WaitForConnectionAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                _outputLoop = Task.Run(() => OutputLoopAsync(_cts!.Token));
            }

            Volatile.Write(ref _state, StateRecording);
            _logger.LogInformation("Recording started (encoder {Encoder}, target {Type}).", options.Encoder, options.CaptureType);
        }
        catch
        {
            await CleanupAfterFailedStartAsync().ConfigureAwait(false);
            Volatile.Write(ref _state, StateFaulted);
            throw;
        }
    }

    private void StartProcess(string ffmpegPath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderrGate)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 20000)
                {
                    _stderr.Remove(0, 10000);
                }
            }

            _logger.LogDebug("ffmpeg: {Line}", e.Data);
        };

        if (!process.Start())
        {
            throw new RecordingException("ffmpeg_start_failed", "无法启动 FFmpeg 进程。");
        }

        _process = process;
        _process.BeginErrorReadLine();

        try
        {
            _job = new WindowsJobObject();
            _job.Assign(process);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job Object 不可用，将依赖显式 kill 清理 FFmpeg。");
            _job = null;
        }
    }

    private async Task AudioPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _audio!.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                _timeline!.AppendRealPcm(chunk.Pcm.Span); // non-blocking; never blocks the WASAPI thread
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording audio capture ended unexpectedly.");
        }
    }

    // Continuous audio output: writes exactly the PCM due per the monotonic clock (real PCM when
    // available, digital silence otherwise) so the audio track stays aligned and never drifts.
    private async Task OutputLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pcm = _timeline!.ProduceUpTo(_clock!.Elapsed);
                if (pcm.Length > 0)
                {
                    using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    writeCts.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await _sink!.WriteAsync(pcm, writeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _audioTimelineFaulted = true;
                        _logger.LogWarning("Audio pipe write stalled (FFmpeg not consuming); stopping recording audio.");
                        break;
                    }
                    catch (IOException ex)
                    {
                        _audioTimelineFaulted = true;
                        _logger.LogWarning(ex, "Audio pipe write failed (FFmpeg gone).");
                        break;
                    }
                }

                if (_timeline!.GetMetrics(_clock!.Elapsed).ClockErrorMs > 2000)
                {
                    _audioTimelineFaulted = true;
                    _logger.LogWarning("Audio timeline fell behind real time (>2 s); stopping recording audio.");
                    break;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio output loop error.");
        }
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken)
    {
        var current = State;
        if (current is RecorderState.Stopped or RecorderState.Faulted && _result is not null)
        {
            return _result;
        }

        if (current != RecorderState.Recording && current != RecorderState.Starting)
        {
            throw new InvalidOperationException("当前没有进行中的录制。");
        }

        Volatile.Write(ref _state, StateStopping);

        var endElapsed = _clock?.Elapsed ?? TimeSpan.Zero;

        // Stop the mic input + the audio output loop, pad the timeline to the session end, then
        // close the pipe so FFmpeg drains the buffered audio tail to EOF before we stop the video.
        try { _pumpCts?.Cancel(); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_audioPump is not null) { try { await _audioPump.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_outputLoop is not null) { try { await _outputLoop.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_audio is not null) { try { await _audio.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ } }

        if (_sink is not null && _timeline is not null && _sink.IsConnected && !_audioTimelineFaulted)
        {
            try
            {
                var tail = _timeline.Flush(endElapsed);
                if (tail.Length > 0)
                {
                    using var wcts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _sink.WriteAsync(tail, wcts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error flushing final audio tail."); }
        }

        if (_sink is not null)
        {
            await _sink.DisposeAsync().ConfigureAwait(false); // audio EOF → FFmpeg drains the buffer
            _sink = null;
        }

        await Task.Delay(300).ConfigureAwait(false); // let FFmpeg read the drained tail

        // Graceful FFmpeg stop: 'q', wait, then kill-tree on timeout.
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteAsync("q").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch { /* process may already be exiting */ }

            var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        _result = await BuildResultAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, StateStopped);

        _job?.Dispose();
        try { process?.Dispose(); } catch { /* ignore */ }
        _cts?.Dispose();
        _pumpCts?.Dispose();

        _logger.LogInformation("Recording stopped. complete={Complete} exit={Exit} bytes={Bytes}.",
            _result.IsComplete, _result.ExitCode, _result.FileSizeBytes);
        return _result;
    }

    private async Task<RecordingResult> BuildResultAsync(CancellationToken cancellationToken)
    {
        var options = _options!;
        int? exitCode = TryGetExitCode(_process);
        long size = File.Exists(options.OutputPath) ? new FileInfo(options.OutputPath).Length : 0;

        FfprobeResult? probe = null;
        if (_ffprobePath is not null && size > 0)
        {
            try { probe = await FFprobe.ProbeAsync(_ffprobePath, options.OutputPath, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ffprobe validation failed."); }
        }

        // Complete only when the file exists, FFmpeg ended acceptably, and (if ffprobe is available)
        // the file is a playable MP4 with a video stream. Never claim success for a broken file.
        bool exitOk = exitCode is 0 or 255; // 'q'/SIGINT-style clean stops
        bool complete;
        string message;
        if (size == 0)
        {
            complete = false;
            message = "输出为 0 字节，录制失败。";
        }
        else if (_ffprobePath is not null)
        {
            complete = exitOk && probe is { IsPlayable: true };
            message = complete ? "录制完成，MP4 可播放。"
                : probe is null ? "无法用 ffprobe 校验，标记为可能不完整。"
                : "MP4 校验未通过（可能不可播放或缺少视频流）。";
        }
        else
        {
            complete = exitOk;
            message = "缺少 ffprobe，未做可播放性校验；文件已保留。";
        }

        return new RecordingResult
        {
            OutputPath = options.OutputPath,
            IsComplete = complete,
            Encoder = options.Encoder,
            ExitCode = exitCode,
            FileSizeBytes = size,
            VideoDuration = probe?.VideoDuration,
            AudioDuration = probe?.AudioDuration,
            Message = message
        };
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static int? TryGetExitCode(Process? process)
    {
        try { return process is { HasExited: true } p ? p.ExitCode : null; }
        catch { return null; }
    }

    private async Task CleanupAfterFailedStartAsync()
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _cts?.Cancel(); } catch { }
        if (_audioPump is not null) { try { await _audioPump.ConfigureAwait(false); } catch { } }
        if (_outputLoop is not null) { try { await _outputLoop.ConfigureAwait(false); } catch { } }
        if (_audio is not null) { try { await _audio.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (_sink is not null) { try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { } }
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { _process.Dispose(); } catch { }
        }

        _job?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (State is RecorderState.Recording or RecorderState.Starting)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
        else
        {
            _job?.Dispose();
        }
    }
}
