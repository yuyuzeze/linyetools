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
    private readonly ConcurrentDictionary<Guid, byte> _disabledSessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sessionCancellation = new();
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

    public async ValueTask EnqueueAsync(TranscriptSegment finalSegment, SessionTranslationOptions session, CancellationToken cancellationToken)
    {
        if (_disabledSessions.ContainsKey(finalSegment.SessionId))
        {
            return;
        }

        if (!TranslationTrigger.ShouldEnqueue(finalSegment, session))
        {
            return;
        }

        // A new job must carry a model — never enqueue an empty-model job (UI-R4A fix).
        if (string.IsNullOrWhiteSpace(session.Model))
        {
            _logger.LogWarning("Skipping translation enqueue: the session snapshot has no model configured.");
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
            // Snapshot the direction + model + prompt version onto the job so recovery re-translates
            // it unchanged (UI-R4A §3/§5), regardless of later settings changes.
            SourceLanguage = session.SourceLanguage,
            TargetLanguage = session.TargetLanguage,
            Model = session.Model,
            PromptVersion = session.PromptVersion,
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
                    if (_disabledSessions.ContainsKey(job.SessionId))
                    {
                        continue;
                    }
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

        if (_disabledSessions.ContainsKey(job.SessionId))
        {
            await MarkCancelledAsync(job).ConfigureAwait(false);
            return;
        }

        var sessionCts = _sessionCancellation.GetOrAdd(job.SessionId, _ => new CancellationTokenSource());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, sessionCts.Token);
        var jobToken = linkedCts.Token;

        if (job.NextAttemptAt is { } na && na > DateTimeOffset.UtcNow)
        {
            return; // not due; the pump will re-signal when it is
        }

        var segment = await _store.GetSegmentAsync(segmentId, jobToken).ConfigureAwait(false);
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
        await _store.UpdateTranslationJobAsync(job, jobToken).ConfigureAwait(false);
        OutcomeChanged?.Invoke(this, new TranslationOutcome(segmentId, TranslationJobState.InProgress, null, TranslationErrorCode.None));

        try
        {
            // Use the JOB's snapshotted direction/model/prompt version, never live options (UI-R4A §3):
            // a mid-session change or a recovered job always translates exactly as enqueued.
            var model = job.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                // Legacy (pre-v4) job with no snapshotted model: fall back to the current config and
                // record a sanitized warning (model NAME only — never the key/prompt/caption text).
                model = _options.Model;
                _logger.LogWarning("Translation job for a segment has no snapshotted model; falling back to current model '{Model}'.", model);
            }

            var request = new TranslationRequest(segment.Text, job.SourceLanguage, job.TargetLanguage, model, job.PromptVersion);
            var translation = await _translator.TranslateAsync(request, jobToken).ConfigureAwait(false);

            if (_disabledSessions.ContainsKey(job.SessionId))
            {
                await MarkCancelledAsync(job).ConfigureAwait(false);
                return;
            }

            await _store.SetSegmentTranslationAsync(segmentId, translation, TranscriptStatus.Translated, jobToken).ConfigureAwait(false);
            await UpdateAsync(job with { State = TranslationJobState.Succeeded, LastErrorCode = null }, TranslationErrorCode.None, translation).ConfigureAwait(false);
        }
        catch (TranslationException tex)
        {
            await HandleFailureAsync(job, segmentId, tex.Code, tex.RetryAfter, jobToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disabledSessions.ContainsKey(job.SessionId))
        {
            await MarkCancelledAsync(job).ConfigureAwait(false);
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

    /// <summary>
    /// Enables or disables translation for a running session. Disabling cancels in-flight HTTP,
    /// marks durable pending jobs cancelled, and prevents races from enqueueing more work.
    /// Re-enabling affects only future final captions.
    /// </summary>
    public async Task SetSessionEnabledAsync(Guid sessionId, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            _disabledSessions.TryRemove(sessionId, out _);
            if (_sessionCancellation.TryRemove(sessionId, out var old))
            {
                old.Dispose();
            }
            _sessionCancellation[sessionId] = new CancellationTokenSource();
            _logger.LogInformation("Translation enabled for running session {Session}.", sessionId);
            return;
        }

        _disabledSessions[sessionId] = 0;
        var cts = _sessionCancellation.GetOrAdd(sessionId, _ => new CancellationTokenSource());
        try { cts.Cancel(); } catch (ObjectDisposedException) { }

        var jobs = await _store.GetJobsForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs.Where(j => j.State is TranslationJobState.Pending
                     or TranslationJobState.InProgress or TranslationJobState.RetryScheduled))
        {
            await MarkCancelledAsync(job).ConfigureAwait(false);
        }
        _logger.LogInformation("Translation disabled for running session {Session}; active jobs cancelled={Count}.",
            sessionId, jobs.Count(j => j.State is TranslationJobState.Pending
                or TranslationJobState.InProgress or TranslationJobState.RetryScheduled));
    }

    private async Task MarkCancelledAsync(TranslationJob job)
    {
        var cancelled = job with
        {
            State = TranslationJobState.Cancelled,
            NextAttemptAt = null,
            LastErrorCode = TranslationErrorCode.Cancelled.ToString(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _store.UpdateTranslationJobAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
        OutcomeChanged?.Invoke(this,
            new TranslationOutcome(job.SegmentId, TranslationJobState.Cancelled, null, TranslationErrorCode.Cancelled));
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
            foreach (var cts in _sessionCancellation.Values)
            {
                cts.Dispose();
            }
            _sessionCancellation.Clear();
            _stopCts?.Dispose();
        }
    }
}
