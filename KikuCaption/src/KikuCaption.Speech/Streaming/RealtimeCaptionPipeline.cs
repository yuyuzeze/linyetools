using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.Stabilization;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.Streaming;

/// <summary>
/// Bounded real-time captioning pipeline (PROJECT.md 6, 9):
/// <c>audio → rolling utterance buffer + energy VAD → periodic transcription (M2 worker) →
/// TranscriptStabilizer + Finalizer → partial/final events</c>.
///
/// One transcription runs at a time (sequential cycle loop), so the worker's timing is never
/// broken by concurrent requests; when inference falls behind real time, cycles simply space out
/// and a back-pressure counter is raised — nothing grows without bound. The model is loaded once.
/// Not coupled to WPF: results are surfaced as events the UI marshals onto its own thread.
/// </summary>
public sealed class RealtimeCaptionPipeline : IAsyncDisposable
{
    private const int BytesPerSecond = 16000 * 2;

    private readonly Func<ISpeechRecognizer> _recognizerFactory;
    private readonly ProgressiveCaptionOptions _options;
    private readonly ISpeechOptionsProvider _speechOptionsProvider;
    private readonly ILogger<RealtimeCaptionPipeline> _logger;

    private readonly object _bufferGate = new();
    private readonly SlidingWindowBuffer _window;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ISpeechRecognizer? _recognizer;
    private TranscriptStabilizer? _stabilizer;
    private Finalizer? _finalizer;
    private ShortFragmentGate? _fragmentGate;
    private CancellationTokenSource? _cts;
    private Task? _ingestTask;
    private Task? _cycleTask;

    private Guid _sessionId;
    private DateTime _utteranceStartUtc;
    private TimeSpan _lastEnd;
    private long _silenceMs;
    private volatile bool _inputEnded;

    // Monotonic-timestamp + seam-dedup state (sliding window).
    private TimeSpan _lastFinalEnd;
    private string _lastFinalText = string.Empty;
    private bool _overlapActive;

    private int _partialCount;
    private int _finalCount;
    private double _rtf;
    private long _lastInferenceMs;
    private long _partialLatencyMs;
    private long _finalLatencyMs;
    private long _skippedCycles;
    private int _stableUnchanged;
    private long _seq;

    private int _state = (int)CaptionPipelineState.Idle;
    private volatile bool _faulted;
    private string? _faultMessage;

    public RealtimeCaptionPipeline(
        Func<ISpeechRecognizer> recognizerFactory,
        ProgressiveCaptionOptions options,
        ISpeechOptionsProvider speechOptionsProvider,
        ILogger<RealtimeCaptionPipeline> logger)
    {
        _recognizerFactory = recognizerFactory;
        _options = options;
        _speechOptionsProvider = speechOptionsProvider;
        _logger = logger;
        _window = new SlidingWindowBuffer(options.WindowSeconds, options.OverlapSeconds);
    }

    public event EventHandler<CaptionPartialEventArgs>? PartialUpdated;
    public event EventHandler<CaptionFinalEventArgs>? FinalProduced;
    public event EventHandler<CaptionFaultedEventArgs>? Faulted;
    public event EventHandler<CaptionPipelineState>? StateChanged;

    public CaptionPipelineState State => (CaptionPipelineState)Volatile.Read(ref _state);

    /// <summary>The session id for the current run (set when StartAsync begins).</summary>
    public Guid SessionId => _sessionId;

    public Task Completion => _completion.Task;

    public CaptionMetrics CurrentMetrics => new()
    {
        PartialCount = _partialCount,
        FinalCount = _finalCount,
        Rtf = _rtf,
        LastInferenceMs = _lastInferenceMs,
        PartialLatencyMs = _partialLatencyMs,
        FinalLatencyMs = _finalLatencyMs,
        QueueDepthMs = QueueDepthMs(),
        SkippedCycles = Interlocked.Read(ref _skippedCycles)
    };

    public async Task StartAsync(IAsyncEnumerable<AudioChunk> audioSource, string language, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, (int)CaptionPipelineState.Starting, (int)CaptionPipelineState.Idle)
            != (int)CaptionPipelineState.Idle)
        {
            throw new InvalidOperationException("实时字幕已在运行或已结束。");
        }

        RaiseStateChanged(CaptionPipelineState.Starting);
        _sessionId = Guid.NewGuid();

        try
        {
            _recognizer = _recognizerFactory();
            // Full config (model/device/compute/beam/cache) plus ONLY this language's prompt/hotwords —
            // a zh session never receives the Japanese context and vice versa.
            await _recognizer.InitializeAsync(_speechOptionsProvider.ForLanguage(language), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetState(CaptionPipelineState.Faulted);
            RaiseStateChanged(CaptionPipelineState.Faulted);
            if (_recognizer is not null)
            {
                await _recognizer.DisposeAsync().ConfigureAwait(false);
                _recognizer = null;
            }

            _completion.TrySetResult();
            throw;
        }

        _stabilizer = new TranscriptStabilizer(_options, _sessionId, language);
        _finalizer = new Finalizer(_options);
        _fragmentGate = new ShortFragmentGate(_options);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetState(CaptionPipelineState.Running);
        RaiseStateChanged(CaptionPipelineState.Running);

        _ingestTask = Task.Run(() => IngestAsync(audioSource, _cts.Token));
        _cycleTask = Task.Run(() => CycleLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var state = State;
        if (state is not (CaptionPipelineState.Running or CaptionPipelineState.Starting))
        {
            return;
        }

        SetState(CaptionPipelineState.Stopping);
        RaiseStateChanged(CaptionPipelineState.Stopping);

        try { _cts?.Cancel(); } catch { /* disposed */ }

        if (_ingestTask is not null || _cycleTask is not null)
        {
            try { await Task.WhenAll(new[] { _ingestTask, _cycleTask }.Where(t => t is not null)!).ConfigureAwait(false); }
            catch { /* observed */ }
        }

        if (_recognizer is not null)
        {
            await _recognizer.DisposeAsync().ConfigureAwait(false);
            _recognizer = null;
        }
    }

    private async Task IngestAsync(IAsyncEnumerable<AudioChunk> source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var span = chunk.Pcm.Span;
                if (span.Length < 2)
                {
                    continue;
                }

                double rms = Rms(span);
                int chunkMs = (int)chunk.Duration.TotalMilliseconds;

                lock (_bufferGate)
                {
                    if (_window.ByteCount == 0)
                    {
                        _utteranceStartUtc = DateTime.UtcNow;
                    }

                    _window.Append(span, chunk.Timestamp);
                }

                if (rms < _options.SilenceRmsThreshold)
                {
                    Interlocked.Add(ref _silenceMs, chunkMs);
                }
                else
                {
                    Interlocked.Exchange(ref _silenceMs, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            _inputEnded = true;
        }
    }

    private async Task CycleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.PartialIntervalMs, cancellationToken).ConfigureAwait(false);

                byte[] snapshot;
                TimeSpan windowStart;
                lock (_bufferGate)
                {
                    // The audio sent to Whisper is the last WindowSeconds of the buffer (capped input).
                    snapshot = _window.TranscriptionWindow(out windowStart);
                }

                bool ended = _inputEnded;

                // Ignore sub-100ms leftovers.
                if (snapshot.Length < BytesPerSecond / 10)
                {
                    if (ended)
                    {
                        break;
                    }

                    continue;
                }

                await RunCycleAsync(snapshot, windowStart, ended, cancellationToken).ConfigureAwait(false);

                if (ended)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping — flush pending below
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
        finally
        {
            await FinalizeFlushAsync().ConfigureAwait(false);

            // Dispose the worker here so any stop path (source end, fault, StopAsync) releases it
            // and leaves no orphan process. DisposeAsync is idempotent.
            var recognizer = _recognizer;
            _recognizer = null;
            if (recognizer is not null)
            {
                try { await recognizer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing recognizer."); }
            }

            SetState(_faulted ? CaptionPipelineState.Faulted : CaptionPipelineState.Stopped);
            RaiseStateChanged(State);
            _completion.TrySetResult();
        }
    }

    private async Task RunCycleAsync(byte[] snapshot, TimeSpan windowStart, bool ended, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string candidate = await TranscribeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        double windowAudioSeconds = snapshot.Length / (double)BytesPerSecond;
        _lastInferenceMs = stopwatch.ElapsedMilliseconds;
        _rtf = windowAudioSeconds > 0 ? stopwatch.Elapsed.TotalSeconds / windowAudioSeconds : 0;
        _partialLatencyMs = stopwatch.ElapsedMilliseconds;
        if (stopwatch.ElapsedMilliseconds > _options.PartialIntervalMs)
        {
            Interlocked.Increment(ref _skippedCycles); // behind real time (back-pressure signal)
        }

        // Absolute end time of buffered audio (windowStart is the tail's start).
        var endTime = windowStart + TimeSpan.FromSeconds(windowAudioSeconds);
        _lastEnd = endTime;

        double utteranceSeconds;
        lock (_bufferGate)
        {
            utteranceSeconds = _window.DurationSeconds;
        }

        var update = new TranscriptUpdate
        {
            SessionId = _sessionId,
            Kind = TranscriptUpdateKind.FinalCandidate,
            StartTime = windowStart,
            EndTime = endTime,
            Text = candidate,
            Sequence = Interlocked.Increment(ref _seq)
        };

        var result = _stabilizer!.Process(update);
        _stableUnchanged = result.StableAdvanced ? 0 : _stableUnchanged + 1;
        _partialCount++;

        PartialUpdated?.Invoke(this, new CaptionPartialEventArgs
        {
            PartialText = result.PartialText,
            StableText = result.StableText
        });

        string pendingForPunct = result.StableText.Length > 0 ? result.StableText : result.PartialText;
        int pendingRunes = CaptionText.SignificantCount(pendingForPunct);
        bool endsWithPunct = CaptionText.EndsWithSentencePunctuation(pendingForPunct);
        var signals = new FinalizerSignals(
            HasPendingText: result.StableText.Trim().Length > 0 || result.PartialText.Trim().Length > 0,
            EndsWithSentencePunctuation: endsWithPunct,
            StableUnchangedCount: _stableUnchanged,
            SilenceMs: (int)Interlocked.Read(ref _silenceMs),
            UtteranceSeconds: utteranceSeconds,
            WaitSeconds: (DateTime.UtcNow - _utteranceStartUtc).TotalSeconds,
            FlushRequested: ended);

        var reason = _finalizer!.Evaluate(signals);
        if (reason != FinalizeReason.None)
        {
            // Briefly hold short, unpunctuated fragments so continuing speech can merge (「まどぐち」),
            // while never losing a genuine short reply like「はい」.
            if (_fragmentGate!.ShouldFinalize(reason, pendingRunes, endsWithPunct, Environment.TickCount64))
            {
                FinalizeCurrent(reason, endTime, slide: false);
            }
        }
        else if (result.StableText.Trim().Length > 0)
        {
            // Continuous speech longer than the window with no natural boundary: commit the stable
            // prefix and advance the window, keeping OverlapSeconds of context (real sliding window).
            lock (_bufferGate)
            {
                if (_window.ExceedsWindow)
                {
                    FinalizeCurrent(FinalizeReason.MaxSentenceLength, endTime, slide: true);
                }
            }
        }
    }

    private void FinalizeCurrent(FinalizeReason reason, TimeSpan endTime, bool slide)
    {
        var segments = _stabilizer!.Flush(endTime);
        foreach (var segment in segments)
        {
            EmitFinal(segment, reason);
        }

        lock (_bufferGate)
        {
            if (slide)
            {
                // Keep OverlapSeconds so the next window continues seamlessly.
                _window.AdvanceKeepingOverlap(endTime);
                _overlapActive = true;
            }
            else
            {
                _window.Clear();
                _overlapActive = false;
            }
        }

        _fragmentGate?.Reset();
        _stableUnchanged = 0;
        Interlocked.Exchange(ref _silenceMs, 0);
    }

    /// <summary>
    /// Emits one final: de-duplicates any leading text carried over an overlapping window seam, and
    /// clamps the timestamps to be monotonic (never before the previous final's end).
    /// </summary>
    private void EmitFinal(TranscriptSegment segment, FinalizeReason reason)
    {
        string text = segment.Text;
        if (_overlapActive && _lastFinalText.Length > 0)
        {
            text = SeamDedup.StripLeadingOverlap(_lastFinalText, text).Trim();
        }

        if (text.Length == 0)
        {
            return; // fully duplicated by the overlap — nothing new to emit
        }

        var start = segment.StartTime < _lastFinalEnd ? _lastFinalEnd : segment.StartTime;
        var end = segment.EndTime < start ? start : segment.EndTime;
        _lastFinalEnd = end;
        _lastFinalText = text;

        _finalCount++;
        _finalLatencyMs = (int)Interlocked.Read(ref _silenceMs);
        FinalProduced?.Invoke(this, new CaptionFinalEventArgs
        {
            Text = text,
            StartTime = start,
            EndTime = end,
            Reason = reason
        });
    }

    private async Task FinalizeFlushAsync()
    {
        if (_stabilizer is null)
        {
            return;
        }

        var segments = _stabilizer.Flush(_lastEnd);
        foreach (var segment in segments)
        {
            EmitFinal(segment, FinalizeReason.FlushRequested);
        }

        await Task.CompletedTask;
    }

    private async Task<string> TranscribeAsync(byte[] snapshot, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var update in _recognizer!.RecognizeAsync(PcmToChunks(snapshot), cancellationToken).ConfigureAwait(false))
        {
            if (update.Kind == TranscriptUpdateKind.FinalCandidate)
            {
                builder.Append(update.Text);
            }
        }

        return builder.ToString();
    }

    private static async IAsyncEnumerable<AudioChunk> PcmToChunks(byte[] pcm,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < pcm.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(ProtocolConstants.MaxAudioBytes, pcm.Length - offset);
            if (take % 2 != 0)
            {
                take -= 1;
            }

            if (take <= 0)
            {
                break;
            }

            yield return new AudioChunk(new ReadOnlyMemory<byte>(pcm, offset, take),
                TimeSpan.FromSeconds(offset / (double)BytesPerSecond),
                TimeSpan.FromSeconds(take / 2.0 / 16000));
            offset += take;
            await Task.Yield();
        }
    }

    private int QueueDepthMs()
    {
        lock (_bufferGate)
        {
            return (int)(_window.ByteCount / (double)BytesPerSecond * 1000);
        }
    }

    private static double Rms(ReadOnlySpan<byte> pcm)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            double v = s / 32768.0;
            sum += v * v;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private void Fault(Exception ex)
    {
        _faulted = true;
        _faultMessage = ex.Message;
        _logger.LogError(ex, "Realtime caption pipeline faulted.");
        Faulted?.Invoke(this, new CaptionFaultedEventArgs { Message = ex.Message });
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private void SetState(CaptionPipelineState state) => Volatile.Write(ref _state, (int)state);

    private void RaiseStateChanged(CaptionPipelineState state) => StateChanged?.Invoke(this, state);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
