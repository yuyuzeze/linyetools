using System.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Translation.Tests;

/// <summary>Queue + SQLite persistence + retry/recovery behavior (M6 §4/§5/§6), real database.</summary>
public sealed class TranslationQueueTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteTranscriptRepository _repo;
    private readonly Guid _sessionId = Guid.NewGuid();

    public TranslationQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kiku_tr_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repo = new SqliteTranscriptRepository(Path.Combine(_root, "k.db"), NullLogger<SqliteTranscriptRepository>.Instance);
    }

    private static TranslationOptions Opts(bool enabled = true, int maxRetries = 3, int concurrency = 1, int queueLen = 100)
        => new()
        {
            Enabled = enabled,
            Endpoint = "https://api.example.internal/v1",
            Model = "m",
            AuthenticationMode = TranslationAuthMode.None,
            MaxRetries = maxRetries,
            MaxConcurrency = concurrency,
            MaxQueueLength = queueLen,
            SourceLanguage = "ja",
            TargetLanguage = "zh"
        };

    private TranslationQueue NewQueue(ScriptedTranslator translator, TranslationOptions options)
        => new(_repo, translator, options, NullLogger<TranslationQueue>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(40), retryBaseDelay: TimeSpan.FromMilliseconds(30));

    // UI-R4A: the per-session immutable direction snapshot passed to EnqueueAsync / ShouldEnqueue.
    private static SessionTranslationOptions Snap(bool enabled = true, string source = "ja", string target = "zh")
        => new(source, target, enabled, "m", 2);

    private async Task<TranscriptSegment> SeedFinalAsync(string text = "今回のリリースについて確認します。", string lang = "ja", int seq = 0)
    {
        // Idempotent (ON CONFLICT DO NOTHING) — safe to call for every seeded segment.
        await _repo.CreateSessionAsync(new MeetingSession
        {
            Id = _sessionId, StartedAt = DateTimeOffset.Now, RecognitionLanguage = lang, OutputDirectory = _root
        }, CancellationToken.None);

        var segment = new TranscriptSegment
        {
            Id = Guid.NewGuid(),
            SessionId = _sessionId,
            StartTime = TimeSpan.FromSeconds(seq),
            EndTime = TimeSpan.FromSeconds(seq + 1),
            Language = lang,
            Text = text,
            Status = TranscriptStatus.Final,
            CreatedAt = DateTimeOffset.Now
        };
        await _repo.UpsertSegmentAsync(segment, CancellationToken.None);
        return segment;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }

    private Task<TranscriptSegment?> ReloadAsync(Guid id) => _repo.GetSegmentAsync(id, CancellationToken.None);
    private async Task<TranslationJob?> JobAsync(Guid segId) =>
        (await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None)).FirstOrDefault(j => j.SegmentId == segId);

    // ---- Trigger rules (queue-level) ----

    [Fact] // triggers 2/3/4/5
    public void ShouldEnqueue_RejectsPartialChineseEmptyDisabled()
    {
        var ja = new TranscriptSegment { Id = Guid.NewGuid(), SessionId = _sessionId, StartTime = default, EndTime = default, Language = "ja", Text = "あ", Status = TranscriptStatus.Final, CreatedAt = DateTimeOffset.Now };
        Assert.True(TranslationTrigger.ShouldEnqueue(ja, Snap()));
        Assert.False(TranslationTrigger.ShouldEnqueue(ja with { Status = TranscriptStatus.Partial }, Snap()));
        Assert.False(TranslationTrigger.ShouldEnqueue(ja with { Language = "zh" }, Snap()));
        Assert.False(TranslationTrigger.ShouldEnqueue(ja with { Text = "  " }, Snap()));
        Assert.False(TranslationTrigger.ShouldEnqueue(ja, Snap(enabled: false)));
        Assert.False(TranslationTrigger.ShouldEnqueue(ja with { Translation = "已翻译" }, Snap()));
    }

    [Fact] // triggers 1: ja final → success end to end
    public async Task JaFinal_Translates_UpdatesSegment_And_Job()
    {
        var seg = await SeedFinalAsync();
        var translator = new ScriptedTranslator((_, _) => Task.FromResult("确认一下本次发布内容。"));
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () => (await ReloadAsync(seg.Id))?.Status == TranscriptStatus.Translated));
        var reloaded = await ReloadAsync(seg.Id);
        Assert.Equal("确认一下本次发布内容。", reloaded!.Translation);
        Assert.Equal(TranslationJobState.Succeeded, (await JobAsync(seg.Id))!.State);
        Assert.Equal(1, translator.Calls);
    }

    [Fact] // triggers 6/7: duplicate final → single job
    public async Task DuplicateEnqueue_ProducesOneJob()
    {
        var seg = await SeedFinalAsync();
        var gate = new TaskCompletionSource();
        var translator = new ScriptedTranslator(async (_, _) => { await gate.Task; return "x"; });
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);

        // Only one active job exists regardless of repeated enqueues.
        var jobs = await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None);
        Assert.Single(jobs.Where(j => j.SegmentId == seg.Id));
        gate.SetResult();
    }

    [Fact] // queue 5/8: retryable failure then success; attempt recorded
    public async Task RetryableFailure_ThenSuccess()
    {
        var seg = await SeedFinalAsync();
        int calls = 0;
        var translator = new ScriptedTranslator((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TranslationException(TranslationErrorCode.ServiceUnavailable, "503");
            }

            return Task.FromResult("成功");
        });
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () => (await ReloadAsync(seg.Id))?.Status == TranscriptStatus.Translated));
        Assert.Equal("成功", (await ReloadAsync(seg.Id))!.Translation);
        Assert.True((await JobAsync(seg.Id))!.AttemptCount >= 1);
    }

    [Fact] // queue 6/16: permanent failure keeps original text
    public async Task PermanentFailure_KeepsOriginal_MarksFailed()
    {
        var seg = await SeedFinalAsync("原文です");
        var translator = new ScriptedTranslator((_, _) => throw new TranslationException(TranslationErrorCode.BadRequest, "400"));
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () => (await JobAsync(seg.Id))?.State == TranslationJobState.FailedPermanent));
        var reloaded = await ReloadAsync(seg.Id);
        Assert.Equal("原文です", reloaded!.Text);         // original intact
        Assert.Null(reloaded.Translation);                 // no translation
        Assert.Equal(TranscriptStatus.TranslationFailed, reloaded.Status);
        Assert.Equal(1, translator.Calls);                 // 400 not retried
    }

    [Fact] // queue 7: max retries exhausted → permanent
    public async Task MaxRetriesExhausted_Permanent()
    {
        var seg = await SeedFinalAsync();
        var translator = new ScriptedTranslator((_, _) => throw new TranslationException(TranslationErrorCode.ServiceUnavailable, "503"));
        await using var queue = NewQueue(translator, Opts(maxRetries: 2));
        await queue.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () => (await JobAsync(seg.Id))?.State == TranslationJobState.FailedPermanent, 8000));
        Assert.Equal(TranscriptStatus.TranslationFailed, (await ReloadAsync(seg.Id))!.Status);
        Assert.True(translator.Calls >= 3); // initial + 2 retries
    }

    [Fact] // queue 10: out-of-order responses update the correct segments
    public async Task OutOfOrderResponses_UpdateCorrectSegments()
    {
        var a = await SeedFinalAsync("一つ目", seq: 1);
        var b = await SeedFinalAsync("二つ目", seq: 2);
        var translator = new ScriptedTranslator(async (text, _) =>
        {
            // First-seen ("一つ目") returns slower than the second, forcing out-of-order completion.
            if (text == "一つ目") { await Task.Delay(120); return "第一"; }
            await Task.Delay(10); return "第二";
        });
        await using var queue = NewQueue(translator, Opts(concurrency: 2));
        await queue.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(a, Snap(), CancellationToken.None);
        await queue.EnqueueAsync(b, Snap(), CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () =>
            (await ReloadAsync(a.Id))?.Translation == "第一" && (await ReloadAsync(b.Id))?.Translation == "第二"));
    }

    [Fact] // queue 11/12/13: recovery of Pending on restart; success not resent
    public async Task Restart_RecoversPending_AndDoesNotResendSucceeded()
    {
        var pending = await SeedFinalAsync("未訳", seq: 1);
        var done = await SeedFinalAsync("既訳", seq: 2);

        // Pre-seed: one Pending job, one already-Succeeded segment+job.
        var now = DateTimeOffset.UtcNow;
        await _repo.CreateTranslationJobAsync(new TranslationJob { Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = pending.Id, State = TranslationJobState.Pending, CreatedAt = now, UpdatedAt = now }, CancellationToken.None);
        await _repo.SetSegmentTranslationAsync(done.Id, "已翻译", TranscriptStatus.Translated, CancellationToken.None);
        await _repo.CreateTranslationJobAsync(new TranslationJob { Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = done.Id, State = TranslationJobState.Succeeded, CreatedAt = now, UpdatedAt = now }, CancellationToken.None);

        var translator = new ScriptedTranslator((_, _) => Task.FromResult("回復訳"));
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None); // recovery re-queues Pending only

        Assert.True(await WaitUntilAsync(async () => (await ReloadAsync(pending.Id))?.Status == TranscriptStatus.Translated));
        Assert.Equal("回復訳", (await ReloadAsync(pending.Id))!.Translation);
        Assert.Equal("已翻译", (await ReloadAsync(done.Id))!.Translation); // untouched
        Assert.All(translator.Inputs, i => Assert.Equal("未訳", i));       // succeeded one never resent
    }

    [Fact] // queue 13b: lingering InProgress recovered to Pending then processed
    public async Task Restart_RecoversInProgress()
    {
        var seg = await SeedFinalAsync("進行中", seq: 1);
        var now = DateTimeOffset.UtcNow;
        await _repo.CreateTranslationJobAsync(new TranslationJob { Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = seg.Id, State = TranslationJobState.InProgress, CreatedAt = now, UpdatedAt = now }, CancellationToken.None);

        var translator = new ScriptedTranslator((_, _) => Task.FromResult("恢复"));
        await using var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(async () => (await ReloadAsync(seg.Id))?.Status == TranscriptStatus.Translated));
    }

    [Fact] // queue 1/2: bounded queue, full does not lose tasks
    public async Task BoundedQueue_Full_DoesNotLoseTasks()
    {
        var segs = new List<TranscriptSegment>();
        for (int i = 1; i <= 10; i++)
        {
            segs.Add(await SeedFinalAsync($"文{i}", seq: i));
        }

        var translator = new ScriptedTranslator((text, _) => Task.FromResult("译-" + text));
        await using var queue = NewQueue(translator, Opts(queueLen: 2)); // tiny channel
        await queue.StartAsync(CancellationToken.None);
        foreach (var s in segs)
        {
            await queue.EnqueueAsync(s, Snap(), CancellationToken.None);
        }

        Assert.True(await WaitUntilAsync(async () =>
        {
            foreach (var s in segs)
            {
                if ((await ReloadAsync(s.Id))?.Status != TranscriptStatus.Translated) return false;
            }
            return true;
        }, 8000));
    }

    [Fact] // queue 15: stop cancels current request but keeps job runnable (not FailedPermanent)
    public async Task Stop_CancelsInFlight_KeepsJobPending()
    {
        var seg = await SeedFinalAsync("停止テスト");
        var started = new TaskCompletionSource();
        var translator = new ScriptedTranslator(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // will be cancelled by dispose
            return "never";
        });
        var queue = NewQueue(translator, Opts());
        await queue.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(seg, Snap(), CancellationToken.None);
        await started.Task; // ensure the request is in flight

        await queue.DisposeAsync(); // stop → cancel current request

        var job = await JobAsync(seg.Id);
        Assert.NotEqual(TranslationJobState.FailedPermanent, job!.State);
        Assert.Equal("停止テスト", (await ReloadAsync(seg.Id))!.Text); // original intact
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
