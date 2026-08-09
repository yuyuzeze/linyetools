using System.Diagnostics;
using KikuCaption.Audio.Wav;
using KikuCaption.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Audio.Capture;

/// <summary>
/// Drives an <see cref="IAudioCaptureService"/> and writes the captured audio to a WAV file
/// via a single background pump task. Handles repeated start/stop, cancellation and faults
/// safely, and disposes all resources deterministically.
/// </summary>
public sealed class SystemAudioWavRecorder : ISystemAudioWavRecorder
{
    private readonly Func<IAudioCaptureService> _captureServiceFactory;
    private readonly ILogger<SystemAudioWavRecorder> _logger;
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = new();

    private IAudioCaptureService? _service;
    private WavFileWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private long _bytesWritten;
    private bool _finished;
    private AudioRecorderState _state = AudioRecorderState.Idle;

    public SystemAudioWavRecorder(
        Func<IAudioCaptureService> captureServiceFactory,
        ILogger<SystemAudioWavRecorder> logger)
    {
        _captureServiceFactory = captureServiceFactory;
        _logger = logger;
    }

    public event EventHandler<AudioRecorderFaultedEventArgs>? Faulted;

    public AudioRecorderState State
    {
        get { lock (_gate) { return _state; } }
    }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public string? OutputPath { get; private set; }

    public Task StartAsync(string outputFilePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new ArgumentException("输出路径不能为空。", nameof(outputFilePath));
        }

        lock (_gate)
        {
            if (_state is AudioRecorderState.Capturing or AudioRecorderState.Stopping)
            {
                throw new InvalidOperationException("已在捕获中，请先停止当前捕获。");
            }

            var fullPath = Path.GetFullPath(outputFilePath);
            if (File.Exists(fullPath))
            {
                // Never overwrite existing user files (PROJECT.md M1 约束 10).
                throw new IOException($"目标文件已存在，拒绝覆盖：{fullPath}");
            }

            _writer = new WavFileWriter(fullPath);
            OutputPath = fullPath;
            _service = _captureServiceFactory();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _bytesWritten, 0);
            _finished = false;
            _state = AudioRecorderState.Capturing;
            _stopwatch.Restart();

            var service = _service;
            var writer = _writer;
            var token = _cts.Token;
            // The single background pump — the only Task.Run in the audio module.
            _pump = Task.Run(() => PumpAsync(service, writer, token));
        }

        _logger.LogInformation("WAV capture started -> {Path}", OutputPath);
        return Task.CompletedTask;
    }

    private async Task PumpAsync(IAudioCaptureService service, WavFileWriter writer, CancellationToken token)
    {
        try
        {
            await foreach (var chunk in service.CaptureAsync(token).ConfigureAwait(false))
            {
                writer.Write(chunk.Pcm);
                Interlocked.Add(ref _bytesWritten, chunk.Pcm.Length);
            }

            await FinishAsync(AudioRecorderState.Stopped).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(AudioRecorderState.Stopped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System audio capture faulted.");
            await FinishAsync(AudioRecorderState.Faulted).ConfigureAwait(false);
            Faulted?.Invoke(this, new AudioRecorderFaultedEventArgs(ex));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? pump;

        lock (_gate)
        {
            if (_state != AudioRecorderState.Capturing)
            {
                return; // idempotent: already stopping/stopped/faulted/idle
            }

            _state = AudioRecorderState.Stopping;
            cts = _cts;
            pump = _pump;
        }

        try { cts?.Cancel(); } catch { /* disposed race */ }

        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); } catch { /* observed in pump */ }
        }

        await FinishAsync(AudioRecorderState.Stopped).ConfigureAwait(false);
        _logger.LogInformation(
            "WAV capture stopped -> {Path} ({Bytes} bytes, {Elapsed}).",
            OutputPath, BytesWritten, Elapsed);
    }

    private async Task FinishAsync(AudioRecorderState finalState)
    {
        IAudioCaptureService? service;
        WavFileWriter? writer;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _stopwatch.Stop();
            service = _service;
            writer = _writer;
            cts = _cts;
            _state = finalState;
        }

        // Dispose the writer first so the WAV header (lengths) is finalized on disk.
        try { writer?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing WAV writer."); }

        if (service is not null)
        {
            try { await service.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing capture service."); }
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await FinishAsync(AudioRecorderState.Stopped).ConfigureAwait(false);
    }
}
