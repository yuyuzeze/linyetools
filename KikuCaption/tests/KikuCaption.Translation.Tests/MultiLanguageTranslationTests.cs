using System.Collections.Concurrent;
using System.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Translation.Tests;

/// <summary>UI-R4A: generic source→target translation — prompt, session snapshot, queue direction.</summary>
public sealed class MultiLanguageTranslationTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteTranscriptRepository _repo;
    private readonly Guid _sessionId = Guid.NewGuid();

    public MultiLanguageTranslationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kiku_r4a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repo = new SqliteTranscriptRepository(Path.Combine(_root, "k.db"), NullLogger<SqliteTranscriptRepository>.Instance);
    }

    // Records the full request each call used — proves the queue uses the JOB snapshot, not live options.
    private sealed class DirectionTranslator : IAiTranslationService
    {
        public readonly ConcurrentBag<(string Source, string Target, string Model, int Version, string Text)> Calls = new();
        public Task<string> TranslateAsync(TranslationRequest req, CancellationToken ct)
        {
            Calls.Add((req.SourceLanguage, req.TargetLanguage, req.Model, req.PromptVersion, req.Text));
            return Task.FromResult($"[{req.SourceLanguage}->{req.TargetLanguage}] {req.Text}");
        }
    }

    private static TranslationOptions LiveOpts() => new()
    {
        Enabled = true, Endpoint = "https://api.example.internal/v1", Model = "m",
        AuthenticationMode = TranslationAuthMode.None,
        SourceLanguage = "ja", TargetLanguage = "zh" // deliberately different from the snapshots below
    };

    private TranslationQueue NewQueue(IAiTranslationService t)
        => new(_repo, t, LiveOpts(), NullLogger<TranslationQueue>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(40), retryBaseDelay: TimeSpan.FromMilliseconds(30));

    private async Task<TranscriptSegment> SeedAsync(string text, string lang, int seq)
    {
        await _repo.CreateSessionAsync(new MeetingSession
        {
            Id = _sessionId, StartedAt = DateTimeOffset.Now, RecognitionLanguage = lang, OutputDirectory = _root
        }, CancellationToken.None);
        var seg = new TranscriptSegment
        {
            Id = Guid.NewGuid(), SessionId = _sessionId,
            StartTime = TimeSpan.FromSeconds(seq), EndTime = TimeSpan.FromSeconds(seq + 1),
            Language = lang, Text = text, Status = TranscriptStatus.Final, CreatedAt = DateTimeOffset.Now
        };
        await _repo.UpsertSegmentAsync(seg, CancellationToken.None);
        return seg;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (await cond()) return true; await Task.Delay(20); }
        return await cond();
    }

    // ---------- Prompt ----------

    [Theory]
    [InlineData("ja", "zh", "Japanese", "Simplified Chinese")]
    [InlineData("ja", "en", "Japanese", "English")]
    [InlineData("zh", "en", "Simplified Chinese", "English")]
    [InlineData("zh", "ja", "Simplified Chinese", "Japanese")]
    public void Prompt_UsesSourceAndTarget(string src, string tgt, string srcName, string tgtName)
    {
        var prompt = TranslationPrompt.BuildSystem(2, src, tgt);
        Assert.Contains($"from {srcName} ({src})", prompt);
        Assert.Contains($"into {tgtName} ({tgt})", prompt);
        Assert.DoesNotContain("日中", prompt);       // no hard-coded JA→ZH assistant (v2)
        Assert.DoesNotContain("翻译助手", prompt);
    }

    [Fact] // the v2 prompt is a pure function of source/target (never varies with UI state)
    public void Prompt_IsDeterministic()
        => Assert.Equal(TranslationPrompt.BuildSystem(2, "ja", "en"), TranslationPrompt.BuildSystem(2, "ja", "en"));

    // ---------- Prompt version dispatch (UI-R4A fix) ----------

    [Fact] // v1 dispatches the legacy JA→ZH prompt; v2 the generic prompt; they differ
    public void PromptVersion_Dispatches()
    {
        var v1 = TranslationPrompt.BuildSystem(1, "ja", "zh");
        var v2 = TranslationPrompt.BuildSystem(2, "ja", "zh");
        Assert.Contains("日中会议实时翻译助手", v1);           // legacy prompt
        Assert.Contains("professional real-time meeting translator", v2); // generic prompt
        Assert.NotEqual(v1, v2);
    }

    [Fact] // an unknown prompt version is not silently upgraded — it throws
    public void PromptVersion_Unknown_Throws()
    {
        Assert.True(TranslationPrompt.IsSupported(1));
        Assert.True(TranslationPrompt.IsSupported(2));
        Assert.False(TranslationPrompt.IsSupported(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranslationPrompt.BuildSystem(99, "ja", "zh"));
    }

    [Fact] // zh target is Simplified Chinese; never Traditional; en/ja unaffected
    public void Zh_IsSimplifiedChinese_NeverTraditional()
    {
        Assert.Equal("Simplified Chinese", TranslationPrompt.LanguageName("zh"));
        Assert.Equal("Japanese", TranslationPrompt.LanguageName("ja"));
        Assert.Equal("English", TranslationPrompt.LanguageName("en"));
        Assert.DoesNotContain("Traditional Chinese", TranslationPrompt.BuildSystem(2, "ja", "zh"));
        Assert.Contains("Simplified Chinese", TranslationPrompt.BuildSystem(2, "ja", "zh"));
    }

    // ---------- Session snapshot ----------

    [Theory]
    [InlineData("ja", "zh", true, true)]   // enabled + different → effective
    [InlineData("ja", "en", true, true)]
    [InlineData("ja", "ja", true, false)]  // same language → NOT effective (but Enabled preference kept)
    [InlineData("zh", "zh", true, false)]
    [InlineData("ja", "zh", false, false)] // disabled
    public void Snapshot_EffectiveEnabled(string src, string tgt, bool enabled, bool effective)
    {
        var snap = new SessionTranslationOptions(src, tgt, enabled, "m", 2);
        Assert.Equal(effective, snap.EffectiveEnabled);
        Assert.Equal(enabled, snap.Enabled);            // preference is never overwritten
        Assert.Equal(src == tgt, snap.IsSameLanguage);
    }

    // ---------- Queue uses the job direction ----------

    [Theory]
    [InlineData("ja", "en")]
    [InlineData("zh", "ja")]
    [InlineData("zh", "en")]
    public async Task Queue_UsesSnapshotDirection_NotLiveOptions(string src, string tgt)
    {
        var seg = await SeedAsync("原文/原文", src, 1);
        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator); // live options say ja→zh
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, new SessionTranslationOptions(src, tgt, true, "m", 2), CancellationToken.None);

        Assert.True(await WaitAsync(async () => (await _repo.GetSegmentAsync(seg.Id, CancellationToken.None))?.Status == TranscriptStatus.Translated));
        var call = Assert.Single(translator.Calls);
        Assert.Equal(src, call.Source);   // the SNAPSHOT direction, not live ja→zh
        Assert.Equal(tgt, call.Target);

        var job = (await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None)).Single(j => j.SegmentId == seg.Id);
        Assert.Equal(src, job.SourceLanguage);
        Assert.Equal(tgt, job.TargetLanguage);
        Assert.Equal(2, job.PromptVersion);
    }

    [Theory] // same-language sessions never create a job or call the API
    [InlineData("ja", "ja")]
    [InlineData("zh", "zh")]
    public async Task Queue_SameLanguage_NoJob_NoCall(string src, string tgt)
    {
        var seg = await SeedAsync("同じ", src, 1);
        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator);
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, new SessionTranslationOptions(src, tgt, true, "m", 2), CancellationToken.None);
        await Task.Delay(200);

        Assert.Empty(translator.Calls);
        Assert.Empty(await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None));
        Assert.Equal("同じ", (await _repo.GetSegmentAsync(seg.Id, CancellationToken.None))!.Text); // original kept
    }

    [Fact] // a recovered job keeps its original direction, ignoring live options (ja→zh)
    public async Task Recovery_UsesJobDirection_NotLiveOptions()
    {
        var seg = await SeedAsync("回復", "zh", 1);
        var now = DateTimeOffset.UtcNow;
        // Pre-seed a Pending zh→ja job (as if from a previous run), while live options are ja→zh.
        await _repo.CreateTranslationJobAsync(new TranslationJob
        {
            Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = seg.Id,
            State = TranslationJobState.Pending, SourceLanguage = "zh", TargetLanguage = "ja",
            PromptVersion = 2, CreatedAt = now, UpdatedAt = now
        }, CancellationToken.None);

        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator);
        await queue.StartAsync(CancellationToken.None); // recovery re-queues the Pending job

        Assert.True(await WaitAsync(async () => (await _repo.GetSegmentAsync(seg.Id, CancellationToken.None))?.Status == TranscriptStatus.Translated));
        var call = Assert.Single(translator.Calls);
        Assert.Equal("zh", call.Source);   // the JOB's original direction, not live ja→zh
        Assert.Equal("ja", call.Target);
    }

    [Theory] // the queue passes the snapshot model + prompt version, never live options
    [InlineData("model-A", 2)]
    [InlineData("model-B", 1)]
    public async Task Queue_UsesSnapshotModelAndVersion(string model, int version)
    {
        var seg = await SeedAsync("モデル", "ja", 1);
        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator); // live model is "m"
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, new SessionTranslationOptions("ja", "zh", true, model, version), CancellationToken.None);

        Assert.True(await WaitAsync(async () => translator.Calls.Any()));
        var call = translator.Calls.Single();
        Assert.Equal(model, call.Model);       // the snapshot model, not "m"
        Assert.Equal(version, call.Version);

        var job = (await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None)).Single(j => j.SegmentId == seg.Id);
        Assert.Equal(model, job.Model);
        Assert.Equal(version, job.PromptVersion);
    }

    [Fact] // a new job must NOT enter the queue with an empty model
    public async Task Queue_EmptyModelSnapshot_NoJob()
    {
        var seg = await SeedAsync("空モデル", "ja", 1);
        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator);
        await queue.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(seg, new SessionTranslationOptions("ja", "zh", true, "", 2), CancellationToken.None);
        await Task.Delay(150);

        Assert.Empty(translator.Calls);
        Assert.Empty(await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None));
    }

    [Fact] // a recovered job uses its persisted model/version/direction, not live options
    public async Task Recovery_UsesJobModelVersionDirection()
    {
        var seg = await SeedAsync("回復モデル", "zh", 1);
        var now = DateTimeOffset.UtcNow;
        await _repo.CreateTranslationJobAsync(new TranslationJob
        {
            Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = seg.Id, State = TranslationJobState.Pending,
            SourceLanguage = "zh", TargetLanguage = "ja", Model = "model-A", PromptVersion = 2,
            CreatedAt = now, UpdatedAt = now
        }, CancellationToken.None);

        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator); // live is ja→zh / "m"
        await queue.StartAsync(CancellationToken.None);

        Assert.True(await WaitAsync(async () => translator.Calls.Any()));
        var call = translator.Calls.Single();
        Assert.Equal("zh", call.Source);
        Assert.Equal("ja", call.Target);
        Assert.Equal("model-A", call.Model);
        Assert.Equal(2, call.Version);
    }

    [Fact] // a legacy job with no snapshotted model falls back to the current configured model
    public async Task Recovery_LegacyEmptyModel_FallsBackToLive()
    {
        var seg = await SeedAsync("旧モデル", "ja", 1);
        var now = DateTimeOffset.UtcNow;
        await _repo.CreateTranslationJobAsync(new TranslationJob
        {
            Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = seg.Id, State = TranslationJobState.Pending,
            SourceLanguage = "ja", TargetLanguage = "zh", Model = "", PromptVersion = 2,
            CreatedAt = now, UpdatedAt = now
        }, CancellationToken.None);

        var translator = new DirectionTranslator();
        await using var queue = NewQueue(translator); // live model "m"
        await queue.StartAsync(CancellationToken.None);

        Assert.True(await WaitAsync(async () => translator.Calls.Any()));
        Assert.Equal("m", translator.Calls.Single().Model); // fell back to the live model
    }

    [Fact] // job direction survives a round-trip through the store
    public async Task Job_PersistsDirection_RoundTrip()
    {
        var seg = await SeedAsync("永続", "zh", 1);
        var now = DateTimeOffset.UtcNow;
        await _repo.CreateTranslationJobAsync(new TranslationJob
        {
            Id = Guid.NewGuid(), SessionId = _sessionId, SegmentId = seg.Id,
            State = TranslationJobState.Pending, SourceLanguage = "zh", TargetLanguage = "en",
            Model = "gpt-x", PromptVersion = 2, CreatedAt = now, UpdatedAt = now
        }, CancellationToken.None);

        var job = (await _repo.GetJobsForSessionAsync(_sessionId, CancellationToken.None)).Single();
        Assert.Equal("zh", job.SourceLanguage);
        Assert.Equal("en", job.TargetLanguage);
        Assert.Equal("gpt-x", job.Model);
        Assert.Equal(2, job.PromptVersion);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
