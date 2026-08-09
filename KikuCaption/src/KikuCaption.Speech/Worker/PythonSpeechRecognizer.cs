using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Protocol;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.Worker;

/// <summary>
/// <see cref="ISpeechRecognizer"/> backed by the resident Python worker. One background loop is
/// the single consumer of the worker's stdout; it routes <c>ready</c>/<c>partial</c>/
/// <c>final_candidate</c>/<c>flushed</c>/<c>error</c> messages. Audio is streamed as it arrives
/// (bounded by the OS pipe, so Base64 never accumulates without bound); the model is loaded once.
/// </summary>
public sealed class PythonSpeechRecognizer : ISpeechRecognizer
{
    private const int StateIdle = 0;
    private const int StateInitialized = 1;
    private const int StateDisposed = 2;

    private readonly IWhisperWorker _worker;
    private readonly ILogger<PythonSpeechRecognizer> _logger;
    private readonly CancellationTokenSource _cts = new();

    private int _state = StateIdle;
    private Guid _sessionId;
    private long _seq;
    private Task? _readLoop;
    private TaskCompletionSource<double>? _readyTcs;
    private Channel<TranscriptUpdate>? _active;
    private volatile Exception? _fault;

    public PythonSpeechRecognizer(IWhisperWorker worker, ILogger<PythonSpeechRecognizer> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    public async Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
    {
        var previous = Interlocked.CompareExchange(ref _state, StateInitialized, StateIdle);
        if (previous == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(PythonSpeechRecognizer));
        }

        if (previous != StateIdle)
        {
            throw new InvalidOperationException("识别器已初始化。");
        }

        if (options.Language is not ("ja" or "zh"))
        {
            throw new SpeechRecognitionException("invalid_language", "识别语言必须为 ja 或 zh。");
        }

        _sessionId = Guid.NewGuid();
        await _worker.StartAsync(cancellationToken).ConfigureAwait(false);

        _readyTcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

        await _worker.SendAsync(new ProtocolMessage
        {
            Type = ProtocolConstants.Types.Initialize,
            SessionId = _sessionId.ToString(),
            Seq = NextSeq(),
            Model = options.Model,
            Device = options.Device,
            ComputeType = options.ComputeType,
            BeamSize = options.BeamSize,
            Language = options.Language,
            ModelCacheDir = options.ModelCacheDirectory
        }, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.InitializeTimeout);
        try
        {
            var loadMs = await _readyTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation("Whisper model ready in {LoadMs} ms (language {Language}).", loadMs, options.Language);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechRecognitionException("timeout",
                $"Worker 初始化/模型加载超时（>{options.InitializeTimeout.TotalSeconds:0}s）。");
        }
    }

    public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();

        // Fail fast if the worker already errored or exited (avoids waiting on a channel that
        // no read loop will ever complete).
        if (_fault is Exception existingFault)
        {
            throw existingFault;
        }

        var active = Channel.CreateBounded<TranscriptUpdate>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _active = active;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var sendTask = Task.Run(() => SendAudioThenFlushAsync(audio, linked.Token), linked.Token);

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
            try { await sendTask.ConfigureAwait(false); } catch { /* surfaced via read path */ }
        }
    }

    private async Task SendAudioThenFlushAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var pcm = chunk.Pcm;
                int offset = 0;
                while (offset < pcm.Length)
                {
                    int take = Math.Min(ProtocolConstants.MaxAudioBytes, pcm.Length - offset);
                    if (take % 2 != 0)
                    {
                        take -= 1;
                    }

                    if (take <= 0)
                    {
                        break;
                    }

                    var message = JsonLinesCodec.CreateAudio(_sessionId.ToString(), NextSeq(), pcm.Slice(offset, take).Span);
                    await _worker.SendAsync(message, cancellationToken).ConfigureAwait(false);
                    offset += take;
                }
            }

            await _worker.SendAsync(new ProtocolMessage
            {
                Type = ProtocolConstants.Types.Flush,
                SessionId = _sessionId.ToString(),
                Seq = NextSeq()
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio send loop ended with error.");
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
                    case ProtocolConstants.Types.Ready:
                        _readyTcs?.TrySetResult(message.ModelLoadMs ?? 0);
                        break;

                    case ProtocolConstants.Types.Partial:
                        await PublishAsync(ToUpdate(message, TranscriptUpdateKind.Partial), cancellationToken).ConfigureAwait(false);
                        break;

                    case ProtocolConstants.Types.FinalCandidate:
                        await PublishAsync(ToUpdate(message, TranscriptUpdateKind.FinalCandidate), cancellationToken).ConfigureAwait(false);
                        break;

                    case ProtocolConstants.Types.Flushed:
                        _active?.Writer.TryComplete();
                        break;

                    case ProtocolConstants.Types.Error:
                        var error = new SpeechRecognitionException(message.Code ?? "error", message.Message ?? "Worker 错误。");
                        _logger.LogWarning("Worker error {Code}: {Message}", message.Code, message.Message);
                        _fault = error;
                        if (_readyTcs is { Task.IsCompleted: false })
                        {
                            _readyTcs.TrySetException(error);
                        }

                        _active?.Writer.TryComplete(error);
                        break;
                }
            }

            var exitFault = _fault ?? new SpeechRecognitionException("worker_exited", "Worker 意外退出。");
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

    private TranscriptUpdate ToUpdate(ProtocolMessage message, TranscriptUpdateKind kind) => new()
    {
        SessionId = _sessionId,
        Kind = kind,
        StartTime = TimeSpan.FromSeconds(message.Start ?? 0),
        EndTime = TimeSpan.FromSeconds(message.End ?? 0),
        Text = message.Text ?? string.Empty,
        Confidence = message.Confidence,
        Sequence = message.Seq
    };

    private void EnsureInitialized()
    {
        var state = Volatile.Read(ref _state);
        if (state == StateDisposed)
        {
            throw new ObjectDisposedException(nameof(PythonSpeechRecognizer));
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

        if (previous == StateInitialized)
        {
            try
            {
                await _worker.SendAsync(new ProtocolMessage
                {
                    Type = ProtocolConstants.Types.Shutdown,
                    SessionId = _sessionId.ToString(),
                    Seq = NextSeq()
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        _cts.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        await _worker.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
