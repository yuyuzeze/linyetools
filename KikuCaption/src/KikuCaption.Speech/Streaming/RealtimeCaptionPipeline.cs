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
/// <c>audio → complete-utterance buffer + energy VAD → periodic full re-transcription (M2 worker) →
/// TranscriptStabilizer + Finalizer → partial/final events</c>.
///
/// <para><b>Data-loss Hotfix (safe buffering):</b> every cycle sends the WHOLE current utterance
/// (everything accumulated since the previous final boundary) to Whisper — never a truncated
/// tail window. Buffer bytes are only ever removed once they have been part of a snapshot that
/// just produced a final, and only the exact number of bytes in that snapshot are removed; any
/// audio appended to the buffer while inference was running (a concurrent <see cref="IngestAsync"/>
/// append) is never touched by that removal and carries over as the start of the next utterance.
/// This guarantees <c>AudioDiscardedUncommittedSeconds</c> (see <see cref="CaptionMetrics"/>) stays
/// 0 on this path. A previous "real sliding window" implementation truncated inference input to the
/// last <c>WindowSeconds</c> and deleted buffer bytes by a fixed overlap byte-count unrelated to how
/// much text had actually stabilized — under fast, continuous speech this silently and irrecoverably
/// discarded several seconds of un-final audio. That path is now gated behind
/// <see cref="ProgressiveCaptionOptions.UseExperimentalSlidingWindow"/>, which
/// <see cref="ProgressiveCaptionOptions.Validate"/> refuses to accept as true.</para>
///
/// One transcription runs at a time (sequential cycle loop), so the worker's timing is never
/// broken by concurrent requests; when inference falls behind real time, cycles simply space out
/// and a back-pressure counter is raised — nothing grows without bound (a long utterance is itself
/// bounded by <c>MaxSentenceSeconds</c>/<c>MaxWaitSeconds</c>, which force a periodic final). The
/// model is loaded once. Not coupled to WPF: results are surfaced as events the UI marshals onto
/// its own thread.
/// </summary>
public sealed class RealtimeCaptionPipeline : IAsyncDisposable
{
    private const int BytesPerSecond = 16000 * 2;

    private readonly Func<ISpeechRecognizer> _recognizerFactory;
    private readonly ProgressiveCaptionOptions _options;
    private readonly ISpeechOptionsProvider _speechOptionsProvider;
    private readonly ILogger<RealtimeCaptionPipeline> _logger;

    // Complete-utterance buffer: everything accumulated since the previous final boundary. Never
    // truncated before being sent to Whisper; only ever shrunk by removing exactly the bytes that
    // were part of a just-finalized snapshot (see class remarks).
    private readonly object _bufferGate = new();
    private readonly List<byte> _utterance = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ISpeechRecognizer? _recognizer;
    private TranscriptStabilizer? _stabilizer;
    private Finalizer? _finalizer;
    private ShortFragmentGate? _fragmentGate;
    private CancellationTokenSource? _cts;
    private Task? _ingestTask;
    private Task? _cycleTask;

    private Guid _sessionId;
    private TimeSpan _utteranceStart;
    private DateTime _utteranceStartUtc;
    private TimeSpan _lastEnd;
    private TimeSpan _lastFinalEnd;
    private long _silenceMs;
    private volatile bool _inputEnded;

    private int _partialCount;
    private int _finalCount;
    private double _rtf;
    private long _lastInferenceMs;
    private long _partialLatencyMs;
    private long _finalLatencyMs;
    private long _skippedCycles;
    private int _stableUnchanged;
    private long _seq;

    // Energy-based hallucination guard (data-loss Hotfix §7): true once any ingested chunk in the
    // CURRENT utterance measured above SilenceRmsThreshold. If an utterance never had any such
    // audio (i.e. was silent for its entire duration), any text the recognizer still returned is
    // treated as a hallucination and suppressed — a content-agnostic signal (not a phrase
    // blacklist). Any loud moment anywhere in the utterance disables the guard, so genuine quiet
    // speech that includes at least a brief louder moment is never suppressed.
    private volatile bool _hadLoudAudioThisUtterance;

    // Audio-accounting diagnostics (data-loss Hotfix). Numbers only — never caption text, prompts,
    // hotwords, or keys. See CaptionMetrics for definitions.
    private long _audioReceivedBytes;
    private long _audioFinalizedBytes;
    private long _audioDiscardedBytes;
#pragma warning disable CS0649 // Intentionally never written on the safe path: FinalizeCurrent only
    // ever removes exactly the bytes of the snapshot it just finalized, so no code path can ever
    // discard un-finalized audio. Kept (and exposed via CurrentMetrics) so the invariant
    // "AudioDiscardedUncommittedSeconds == 0" is directly, objectively assertable by tests/logs.
    private long _audioDiscardedUncommittedBytes;
#pragma warning restore CS0649
    private long _latestSnapshotBytesThisUtterance;
    private long _emptyCandidateCount;

    private int _state = (int)CaptionPipelineState.Idle;
    private volatile bool _faulted;
    private string? _faultMessage;

    public RealtimeCaptionPipeline(
        Func<ISpeechRecognizer> recognizerFactory,
        ProgressiveCaptionOptions options,
        ISpeechOptionsProvider speechOptionsProvider,
        ILogger<RealtimeCaptionPipeline> logger)
    {
        if (options.UseExperimentalSlidingWindow)
        {
            // Defense in depth: Validate() (called at DI startup) already rejects this, but a
            // pipeline can in principle be constructed with an options instance that skipped it.
            throw new NotSupportedException(
                "UseExperimentalSlidingWindow=true 已知会丢失音频，禁止用于构造 RealtimeCaptionPipeline。");
        }

        _recognizerFactory = recognizerFactory;
        _options = options;
        _speechOptionsProvider = speechOptionsProvider;
        _logger = logger;
    }

    public event EventHandler<CaptionPartialEventArgs>? PartialUpdated;
    public event EventHandler<CaptionFinalEventArgs>? FinalProduced;
    public event EventHandler<CaptionFaultedEventArgs>? Faulted;
    public event EventHandler<CaptionPipelineState>? StateChanged;

    public CaptionPipelineState State => (CaptionPipelineState)Volatile.Read(ref _state);

    /// <summary>The session id for the current run (set when StartAsync begins).</summary>
    public Guid SessionId => _sessionId;

    public Task Completion => _completion.Task;

    public CaptionMetrics CurrentMetrics
    {
        get
        {
            double included = (Interlocked.Read(ref _audioFinalizedBytes) + Interlocked.Read(ref _latestSnapshotBytesThisUtterance))
                / (double)BytesPerSecond;
            return new CaptionMetrics
            {
                PartialCount = _partialCount,
                FinalCount = _finalCount,
                Rtf = _rtf,
                LastInferenceMs = _lastInferenceMs,
                PartialLatencyMs = _partialLatencyMs,
                FinalLatencyMs = _finalLatencyMs,
                QueueDepthMs = QueueDepthMs(),
                SkippedCycles = Interlocked.Read(ref _skippedCycles),
                AudioReceivedSeconds = Interlocked.Read(ref _audioReceivedBytes) / (double)BytesPerSecond,
                AudioIncludedInSnapshotsSeconds = included,
                AudioFinalizedSeconds = Interlocked.Read(ref _audioFinalizedBytes) / (double)BytesPerSecond,
                AudioDiscardedSeconds = Interlocked.Read(ref _audioDiscardedBytes) / (double)BytesPerSecond,
                AudioDiscardedUncommittedSeconds = Interlocked.Read(ref _audioDiscardedUncommittedBytes) / (double)BytesPerSecond,
                PendingAudioSeconds = PendingAudioSecondsNow(),
                EmptyCandidateCount = Interlocked.Read(ref _emptyCandidateCount)
            };
        }
    }

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
                    if (_utterance.Count == 0)
                    {
                        _utteranceStart = chunk.Timestamp;
                        _utteranceStartUtc = DateTime.UtcNow;
                        _hadLoudAudioThisUtterance = false; // fresh utterance: reset the guard
                    }

                    _utterance.AddRange(span);
                    _audioReceivedBytes += span.Length;
                }

                if (rms < _options.SilenceRmsThreshold)
                {
                    Interlocked.Add(ref _silenceMs, chunkMs);
                }
                else
                {
                    Interlocked.Exchange(ref _silenceMs, 0);
                    _hadLoudAudioThisUtterance = true;
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
            // Volatile write LAST, strictly after every append above — CycleLoopAsync relies on
            // reading this flag BEFORE taking its buffer snapshot so that once it observes `true`,
            // the very next snapshot is guaranteed to include everything ever ingested (no tail loss
            // at shutdown; see CycleLoopAsync).
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

                // Read `ended` BEFORE the snapshot (not after): _inputEnded is set only once Ingest
                // has appended every chunk, so observing it here first and only then copying the
                // buffer guarantees the snapshot reflects ALL ingested audio when this is the final
                // cycle — otherwise a last chunk appended between the snapshot and this read could be
                // silently left behind when the loop breaks.
                bool ended = _inputEnded;

                byte[] snapshot;
                int snapshotLen;
                TimeSpan utteranceStart;
                lock (_bufferGate)
                {
                    snapshotLen = _utterance.Count;
                    snapshot = _utterance.ToArray(); // the COMPLETE current utterance — never truncated
                    utteranceStart = _utteranceStart;
                    _latestSnapshotBytesThisUtterance = Math.Max(_latestSnapshotBytesThisUtterance, snapshotLen);
                }

                // Ignore sub-100ms leftovers (below one phoneme; not a meaningful content loss).
                if (snapshot.Length < BytesPerSecond / 10)
                {
                    if (ended)
                    {
                        if (snapshot.Length > 0)
                        {
                            // Documented, bounded (<100ms) tail that never got a chance to be
                            // transcribed. Tracked honestly as AudioDiscardedSeconds — NOT counted as
                            // "uncommitted" loss (that metric is reserved for genuine bugs).
                            _audioDiscardedBytes += snapshot.Length;
                        }

                        break;
                    }

                    continue;
                }

                await RunCycleAsync(snapshot, snapshotLen, utteranceStart, ended, cancellationToken).ConfigureAwait(false);

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

    private async Task RunCycleAsync(byte[] snapshot, int snapshotLen, TimeSpan utteranceStart, bool ended, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string candidate = await TranscribeAsync(snapshot, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (string.IsNullOrWhiteSpace(candidate))
        {
            Interlocked.Increment(ref _emptyCandidateCount);
        }

        double audioSeconds = snapshot.Length / (double)BytesPerSecond;
        _lastInferenceMs = stopwatch.ElapsedMilliseconds;
        _rtf = audioSeconds > 0 ? stopwatch.Elapsed.TotalSeconds / audioSeconds : 0;
        _partialLatencyMs = stopwatch.ElapsedMilliseconds;
        if (stopwatch.ElapsedMilliseconds > _options.PartialIntervalMs)
        {
            Interlocked.Increment(ref _skippedCycles); // behind real time (back-pressure signal)
        }

        // The snapshot is the COMPLETE utterance so far, so this end time is the true end of
        // everything transcribed this cycle — not a truncated window's end.
        var endTime = utteranceStart + TimeSpan.FromSeconds(audioSeconds);
        _lastEnd = endTime;

        var update = new TranscriptUpdate
        {
            SessionId = _sessionId,
            Kind = TranscriptUpdateKind.FinalCandidate,
            StartTime = utteranceStart,
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
            UtteranceSeconds: audioSeconds, // the FULL backlog, correctly bounds MaxSentenceSeconds/MaxWaitSeconds
            WaitSeconds: (DateTime.UtcNow - _utteranceStartUtc).TotalSeconds,
            FlushRequested: ended);

        var reason = _finalizer!.Evaluate(signals);
        if (reason == FinalizeReason.None)
        {
            return; // keep accumulating; nothing is ever truncated or advanced away in this mode
        }

        // Briefly hold short, unpunctuated fragments so continuing speech can merge (「まどぐち」),
        // while never losing a genuine short reply like「はい」. Holding never discards audio — the
        // buffer keeps growing while held.
        if (_fragmentGate!.ShouldFinalize(reason, pendingRunes, endsWithPunct, Environment.TickCount64))
        {
            FinalizeCurrent(reason, endTime, snapshotLen);
        }
    }

    /// <summary>
    /// Emits the final(s) for this snapshot, then removes EXACTLY <paramref name="snapshotLen"/>
    /// bytes from the front of the buffer — never more. Any bytes appended by
    /// <see cref="IngestAsync"/> while this cycle's inference was running are, by construction,
    /// beyond that count and are therefore preserved as the start of the next utterance.
    /// </summary>
    private void FinalizeCurrent(FinalizeReason reason, TimeSpan endTime, int snapshotLen)
    {
        var segments = _stabilizer!.Flush(endTime);
        if (_hadLoudAudioThisUtterance)
        {
            foreach (var segment in segments)
            {
                EmitFinal(segment, reason);
            }
        }
        else if (segments.Count > 0)
        {
            // Energy-based hallucination guard: this utterance never had any audio above the
            // silence threshold, yet the recognizer still returned text — suppress it. Audio
            // accounting below is unaffected (the bytes are still accounted as finalized; nothing
            // about this utterance was ever un-transcribed).
            _logger.LogDebug("Suppressed a final from a silent utterance (hallucination guard).");
        }

        lock (_bufferGate)
        {
            int remove = Math.Min(snapshotLen, _utterance.Count); // defensive clamp; should always be equal or less
            _utterance.RemoveRange(0, remove);
            _audioFinalizedBytes += remove;
            _latestSnapshotBytesThisUtterance = 0;

            // Whatever remains arrived DURING this cycle's inference — it is NOT discarded. The next
            // utterance starts exactly where this one's finalized snapshot ended (an explicit,
            // unambiguous boundary — never inferred from text/Stable Prefix).
            _utteranceStart = _utterance.Count > 0 ? endTime : default;
        }

        _fragmentGate?.Reset();
        _stableUnchanged = 0;
        Interlocked.Exchange(ref _silenceMs, 0);
        _utteranceStartUtc = DateTime.UtcNow; // reset the wait-clock for the next utterance
    }

    /// <summary>Emits one final, clamping timestamps to stay monotonic (never before the previous final's end).</summary>
    private void EmitFinal(TranscriptSegment segment, FinalizeReason reason)
    {
        if (segment.Text.Length == 0)
        {
            return;
        }

        var start = segment.StartTime < _lastFinalEnd ? _lastFinalEnd : segment.StartTime;
        var end = segment.EndTime < start ? start : segment.EndTime;
        _lastFinalEnd = end;

        _finalCount++;
        _finalLatencyMs = (int)Interlocked.Read(ref _silenceMs);
        FinalProduced?.Invoke(this, new CaptionFinalEventArgs
        {
            Text = segment.Text,
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
            return (int)(_utterance.Count / (double)BytesPerSecond * 1000);
        }
    }

    private double PendingAudioSecondsNow()
    {
        lock (_bufferGate)
        {
            return _utterance.Count / (double)BytesPerSecond;
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
