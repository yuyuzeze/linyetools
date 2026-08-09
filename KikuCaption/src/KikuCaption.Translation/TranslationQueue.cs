using System.Collections.Concurrent;
using System.Threading.Channels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Translation;

/// <summary>
/// Bounded, durable, background JA→ZH translation queue (M6 §4/§6). SQLite is the reliable source of
/// truth (a Pending/RetryScheduled row is never lost); a bounded in-memory channel plus a periodic
/// pump are only the wake/scheduling mechanism, so a full channel back-pressures the scheduler rather
/// than dropping tasks. Enqueue never blocks recognition, capture, recording, original-text storage,
/// or the UI thread. Retries use exponential backoff + jitter and honor Retry-After; one active job
/// per segment; the original text is never modified on failure.
/// </summary>
public sealed class TranslationQueue : ITranslationQueue, IAsyncDisposable
{
    private readonly ITranslationJobStore _store;
    private readonly IAiTranslationService _translator;
    private readonly TranslationOptions _options;
    private readonly ILogger<TranslationQueue> _logger;
    private readonly Random _rng = new();

    private readonly Channel<Guid> _signals;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _retryBaseDelay;

    private CancellationTokenSource? _stopCts;
    private Task[] _workers = Array.Empty<Task>();
    private Task? _pump;
    private bool _started;

    /// <summary>Raised on every job state transition so the UI can update the card in place.</summary>
    public event EventHandler<TranslationOutcome>? OutcomeChanged;

    public TranslationQueue(
        ITranslationJobStore store,
        IAiTranslationService translator,
        TranslationOptions options,
        ILogger<TranslationQueue> logger,
        TimeSpan? pollInterval = null,
        TimeSpan? retryBaseDelay = null)
    {
        _store = store;
        _translator = translator;
        _options = options;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        _signals = Channel.CreateBounded<Guid>(new BoundedChannelOptions(_options.EffectiveQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Recovers durable jobs (InProgress → Pending, then re-queues Pending/RetryScheduled) and starts
    /// the worker(s) and pump. Idempotent recovery, safe after a crash or normal restart.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        int recovered = await _store.RecoverInProgressJobsAsync(cancellationToken).ConfigureAwait(false);
        if (recovered > 0)
        {
            _logger.LogInformation("Recovered {Count} InProgress translation job(s) to Pending.", recovered);
        }

        _stopCts = new CancellationTokenSource();
        var token = _stopCts.Token;

        _workers = Enumerable.Range(0, _options.EffectiveConcurrency)
            .Select(_ => Task.Run(() => WorkerLoopAsync(token), token))
            .ToArray();
        _pump = Task.Run(() => PumpLoopAsync(token), token);
    }

    public async ValueTask EnqueueAsync(TranscriptSegment finalSegment, CancellationToken cancellationToken)
    {
        if (!TranslationTrigger.ShouldEnqueue(finalSegment, _options))
        {
            return;
        }

        // Skip if an active job already exists (no duplicate task per segment).
        var existing = await _store.GetActiveJobForSegmentAsync(finalSegment.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new TranslationJob
        {
            Id = Guid.NewGuid(),
            SessionId = finalSegment.SessionId,
            SegmentId = finalSegment.Id,
            State = TranslationJobState.Pending,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        try
        {
            await _store.CreateTranslationJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A concurrent insert won the active-per-segment race; treat as already queued.
            _logger.LogDebug(ex, "Translation job for segment already exists; skipping enqueue.");
            return;
        }

        OutcomeChanged?.Invoke(this, new TranslationOutcome(job.SegmentId, TranslationJobState.Pending, null, TranslationErrorCode.None));

        // Best-effort wake; if the bounded channel is full the pump will pick the durable row up.
        _signals.Writer.TryWrite(job.SegmentId);
    }

    private async Task PumpLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var jobs = await _store.GetResumableJobsAsync(token).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                foreach (var job in jobs)
                {
                    if (job.NextAttemptAt is { } na && na > now)
                    {
                        continue; // RetryScheduled, not due yet
                    }

                    if (!_inFlight.ContainsKey(job.SegmentId))
                    {
                        _signals.Writer.TryWrite(job.SegmentId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Translation pump iteration failed; will retry.");
            }

            try
            {
                await Task.Delay(_pollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var segmentId in _signals.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (!_inFlight.TryAdd(segmentId, 0))
                {
                    continue; // another worker already has this segment
                }

                try
                {
                    await ProcessSegmentAsync(segmentId, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Translation worker error for a segment.");
                }
                finally
                {
                    _inFlight.TryRemove(segmentId, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task ProcessSegmentAsync(Guid segmentId, CancellationToken token)
    {
        var job = await _store.GetActiveJobForSegmentAsync(segmentId, token).ConfigureAwait(false);
        if (job is null)
        {
            return; // already terminal
        }

        if (job.NextAttemptAt is { } na && na > DateTimeOffset.UtcNow)
        {
            return; // not due; the pump will re-signal when it is
        }

        var segment = await _store.GetSegmentAsync(segmentId, token).ConfigureAwait(false);
        if (segment is null)
        {
            await UpdateAsync(job with { State = TranslationJobState.FailedPermanent, LastErrorCode = TranslationErrorCode.InvalidConfig.ToString() }, TranslationErrorCode.InvalidConfig, null).ConfigureAwait(false);
            return;
        }

        if (segment.Status == TranscriptStatus.Translated || !string.IsNullOrWhiteSpace(segment.Translation))
        {
            await UpdateAsync(job with { State = TranslationJobState.Succeeded }, TranslationErrorCode.None, segment.Translation).ConfigureAwait(false);
            return;
        }

        // Mark InProgress and surface "translating".
        job = job with { State = TranslationJobState.InProgress, UpdatedAt = DateTimeOffset.UtcNow };
        await _store.UpdateTranslationJobAsync(job, token).ConfigureAwait(false);
        OutcomeChanged?.Invoke(this, new TranslationOutcome(segmentId, TranslationJobState.InProgress, null, TranslationErrorCode.None));

        try
        {
            var translation = await _translator.TranslateAsync(
                segment.Text, _options.SourceLanguage, _options.TargetLanguage, token).ConfigureAwait(false);

            await _store.SetSegmentTranslationAsync(segmentId, translation, TranscriptStatus.Translated, token).ConfigureAwait(false);
            await UpdateAsync(job with { State = TranslationJobState.Succeeded, LastErrorCode = null }, TranslationErrorCode.None, translation).ConfigureAwait(false);
        }
        catch (TranslationException tex)
        {
            await HandleFailureAsync(job, segmentId, tex.Code, tex.RetryAfter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping: keep the job durable and re-runnable next start (do not fail it).
            await _store.UpdateTranslationJobAsync(
                job with { State = TranslationJobState.Pending, UpdatedAt = DateTimeOffset.UtcNow },
                CancellationToken.None).ConfigureAwait(false);
            OutcomeChanged?.Invoke(this, new TranslationOutcome(segmentId, TranslationJobState.Pending, null, TranslationErrorCode.None));
        }
    }

    private async Task HandleFailureAsync(TranslationJob job, Guid segmentId, TranslationErrorCode code, TimeSpan? retryAfter, CancellationToken token)
    {
        if (code == TranslationErrorCode.Cancelled)
        {
            await _store.UpdateTranslationJobAsync(
                job with { State = TranslationJobState.Pending, UpdatedAt = DateTimeOffset.UtcNow },
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        int attempts = job.AttemptCount + 1;
        if (code.IsRetryable() && attempts <= _options.EffectiveMaxRetries)
        {
            var delay = TranslationBackoff.ComputeDelay(attempts, retryAfter, _rng, _retryBaseDelay);
            var next = DateTimeOffset.UtcNow + delay;
            await UpdateAsync(
                job with { State = TranslationJobState.RetryScheduled, AttemptCount = attempts, NextAttemptAt = next, LastErrorCode = code.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
                code, null).ConfigureAwait(false);
            _logger.LogWarning("Translation retry {Attempt}/{Max} scheduled in {Delay:0.0}s ({Code}).",
                attempts, _options.EffectiveMaxRetries, delay.TotalSeconds, code);
        }
        else
        {
            // Permanent: keep original text, mark the segment TranslationFailed.
            await _store.SetSegmentTranslationAsync(segmentId, null, TranscriptStatus.TranslationFailed, token).ConfigureAwait(false);
            await UpdateAsync(
                job with { State = TranslationJobState.FailedPermanent, AttemptCount = attempts, NextAttemptAt = null, LastErrorCode = code.ToString(), UpdatedAt = DateTimeOffset.UtcNow },
                code, null).ConfigureAwait(false);
            _logger.LogWarning("Translation permanently failed ({Code}) after {Attempt} attempt(s).", code, attempts);
        }
    }

    private async Task UpdateAsync(TranslationJob job, TranslationErrorCode code, string? translation)
    {
        await _store.UpdateTranslationJobAsync(job, CancellationToken.None).ConfigureAwait(false);
        OutcomeChanged?.Invoke(this, new TranslationOutcome(job.SegmentId, job.State, translation, code));
    }

    public async ValueTask DisposeAsync()
    {
        try { _stopCts?.Cancel(); } catch { /* ignore */ }

        try
        {
            _signals.Writer.TryComplete();
            var pending = new List<Task>(_workers);
            if (_pump is not null)
            {
                pending.Add(_pump);
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Translation queue shutdown."); }
        finally
        {
            _stopCts?.Dispose();
        }
    }
}
