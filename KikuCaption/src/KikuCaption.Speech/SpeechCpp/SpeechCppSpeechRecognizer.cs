using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// <see cref="ISpeechRecognizer"/> backed by the native <c>speech.exe</c> worker (FastConformer/CTC).
/// Each <see cref="RecognizeAsync"/> call is ONE speech.cpp recognition request: its audio becomes
/// ordered <c>chunk</c> lines, a <c>transcribe</c> completes it, and the single terminal <c>end</c>
/// is mapped to <see cref="TranscriptUpdateKind.FinalCandidate"/> update(s) — one per timed segment
/// when timestamps were requested, otherwise one for the whole utterance. This keeps the exact same
/// outward contract as the Whisper recognizer, so the pipeline and WAV view consume it unchanged.
/// The model is loaded once at <see cref="InitializeAsync"/> and the worker is reused.
/// </summary>
public sealed class SpeechCppSpeechRecognizer : ISpeechRecognizer
{
    private const int StateIdle = 0;
    private const int StateInitialized = 1;
    private const int StateDisposed = 2;

    private const int BytesPerSecond = 16000 * 2; // 16 kHz, mono, int16
    private const int MaxChunkBytes = 262144;      // even; keeps a single JSON line reasonable

    private readonly ISpeechCppWorker _worker;
    private readonly ILogger<SpeechCppSpeechRecognizer> _logger;
    private readonly CancellationTokenSource _cts = new();

    private int _state = StateIdle;
    private long _requestCounter;
    private long _updateSeq;
    private Task? _readLoop;
    private TaskCompletionSource<bool>? _readyTcs;
    private Channel<TranscriptUpdate>? _active;
    private volatile Exception? _fault;

    private Guid _sessionId;
    private string? _keywords;
    private bool _requestTimestamps;

    // Per-request state (one request at a time; the pipeline awaits each fully before the next).
    private volatile string? _currentRequestId;
    private double _currentDurationSec;

    public SpeechCppSpeechRecognizer(ISpeechCppWorker worker, ILogger<SpeechCppSpeechRecognizer> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public async Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateInitialized, StateIdle);
        if (previous == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(SpeechCppSpeechRecognizer));
        }

        if (previous != StateIdle)
        {
            throw new InvalidOperationException("识别器已初始化。");
        }

        if (options.Engine != SpeechEngine.SpeechCpp)
        {
            throw new SpeechRecognitionException("wrong_engine", "SpeechCpp 识别器收到了非 SpeechCpp 的配置。");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new SpeechRecognitionException("model_missing", "未指定 speech.cpp 模型。");
        }

        _sessionId = Guid.NewGuid();
        _keywords = string.IsNullOrWhiteSpace(options.Keywords) ? null : options.Keywords;
        _requestTimestamps = options.RequestTimestamps;

        await _worker.StartAsync(options.Model, cancellationToken).ConfigureAwait(false);

        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.InitializeTimeout);
        try
        {
            await _readyTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation("speech.cpp model ready (model {Model}).", options.Model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechRecognitionException("timeout",
                $"speech.cpp worker 初始化/模型加载超时（>{options.InitializeTimeout.TotalSeconds:0}s）。");
        }
    }

    public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        if (_fault is Exception existingFault)
        {
            throw existingFault;
        }

        var requestId = "r" + Interlocked.Increment(ref _requestCounter).ToString();
        _currentRequestId = requestId;
        _currentDurationSec = 0;

        var active = Channel.CreateBounded<TranscriptUpdate>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _active = active;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var sendTask = Task.Run(() => SendAudioThenTranscribeAsync(requestId, audio, linked.Token), linked.Token);

        try
        {
            await foreach (var update in active.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            _active = null;
            _currentRequestId = null;
            try { await sendTask.ConfigureAwait(false); } catch { /* surfaced via read path */ }
        }
    }

    private async Task SendAudioThenTranscribeAsync(string requestId, IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
    {
        try
        {
            long seq = 0;
            long totalBytes = 0;
            await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var pcm = chunk.Pcm;
                int offset = 0;
                while (offset < pcm.Length)
                {
                    int take = Math.Min(MaxChunkBytes, pcm.Length - offset);
                    if (take % 2 != 0)
                    {
                        take -= 1;
                    }

                    if (take <= 0)
                    {
                        break;
                    }

                    var slice = pcm.Slice(offset, take);
                    await _worker.SendAsync(new SpeechCppMessage
                    {
                        Type = "chunk",
                        Id = requestId,
                        Seq = seq++,
                        Pcm = Convert.ToBase64String(slice.Span)
                    }, cancellationToken).ConfigureAwait(false);

                    offset += take;
                    totalBytes += take;
                }
            }

            _currentDurationSec = totalBytes / (double)BytesPerSecond;

            // Haas forbids keywords together with timestamps, so timestamps win when both are set.
            bool withTimestamps = _requestTimestamps;
            await _worker.SendAsync(new SpeechCppMessage
            {
                Type = "transcribe",
                Id = requestId,
                SampleRate = 16000,
                Timestamps = withTimestamps ? true : null,
                Keywords = withTimestamps ? null : _keywords
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping — try to cancel the in-flight request so the worker frees its id.
            try { await _worker.SendAsync(new SpeechCppMessage { Type = "cancel", Id = requestId }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            _fault = ex;
            _active?.Writer.TryComplete(ex);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _worker.ReadMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (message.Type)
                {
                    case "ready":
                        _readyTcs?.TrySetResult(true);
                        break;

                    case "end":
                        if (message.Id == _currentRequestId)
                        {
                            foreach (var update in ToUpdates(message))
                            {
                                await PublishAsync(update, cancellationToken).ConfigureAwait(false);
                            }

                            _active?.Writer.TryComplete();
                        }

                        break;

                    case "cancelled":
                        if (message.Id == _currentRequestId)
                        {
                            _active?.Writer.TryComplete();
                        }

                        break;

                    case "error":
                    case "fatal":
                        var code = message.Error?.Code ?? "error";
                        var text = message.Error?.Message ?? "speech.cpp worker 错误。";
                        var error = new SpeechRecognitionException(code, text);
                        _logger.LogWarning("speech.cpp error {Code}: {Message}", code, text);
                        _fault = error;
                        if (_readyTcs is { Task.IsCompleted: false })
                        {
                            _readyTcs.TrySetException(error);
                        }

                        _active?.Writer.TryComplete(error);
                        break;

                    // "progress" and worker-side "chunk" (synthesis/dialogue) are not used by ASR.
                }
            }

            var exitFault = _fault ?? new SpeechRecognitionException("worker_exited", "speech.cpp worker 意外退出。");
            _fault = exitFault;
            FaultPending(exitFault);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _fault = ex;
            FaultPending(ex);
        }
    }

    private IEnumerable<TranscriptUpdate> ToUpdates(SpeechCppMessage end)
    {
        if (end.Segments is { Count: > 0 } segments)
        {
            foreach (var seg in segments)
            {
                yield return new TranscriptUpdate
                {
                    SessionId = _sessionId,
                    Kind = TranscriptUpdateKind.FinalCandidate,
                    StartTime = TimeSpan.FromSeconds(seg.Start),
                    EndTime = TimeSpan.FromSeconds(seg.End),
                    Text = seg.Text ?? string.Empty,
                    Sequence = Interlocked.Increment(ref _updateSeq)
                };
            }

            yield break;
        }

        yield return new TranscriptUpdate
        {
            SessionId = _sessionId,
            Kind = TranscriptUpdateKind.FinalCandidate,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(_currentDurationSec),
            Text = end.Text ?? string.Empty,
            Sequence = Interlocked.Increment(ref _updateSeq)
        };
    }

    private void FaultPending(Exception exception)
    {
        if (_readyTcs is { Task.IsCompleted: false })
        {
            _readyTcs.TrySetException(exception);
        }

        _active?.Writer.TryComplete(exception);
    }

    private async Task PublishAsync(TranscriptUpdate update, CancellationToken cancellationToken)
    {
        var active = _active;
        if (active is null)
        {
            return;
        }

        try
        {
            await active.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // recognition ended
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void EnsureInitialized()
    {
        var state = Volatile.Read(ref _state);
        if (state == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(SpeechCppSpeechRecognizer));
        }

        if (state != StateInitialized)
        {
            throw new InvalidOperationException("识别器尚未初始化。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.Exchange(ref _state, StateDisposed);
        if (previous == StateDisposed)
        {
            return;
        }

        try { _cts.Cancel(); } catch { /* ignore */ }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        await _worker.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
