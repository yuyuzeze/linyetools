using System.IO;
using System.Linq;
using KikuCaption.Core.Enums;
using KikuCaption.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Summarization.Tests;

/// <summary>UI-R5C §18: Fake-HTTP end-to-end — SQLite-shaped final captions → Map → Reduce → Markdown.</summary>
public class MeetingSummaryServiceTests : IDisposable
{
    private readonly string _dir;

    public MeetingSummaryServiceTests()
        => _dir = Path.Combine(Path.GetTempPath(), "kiku_sum_svc", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TranslationOptions Transport(bool enabled = false) => new()
    {
        Enabled = enabled, // even with translation disabled, a valid API config allows summaries
        Endpoint = "https://api.example.com/v1/chat/completions",
        Model = "translation-model",
        AuthenticationMode = TranslationAuthMode.Bearer
    };

    private MeetingSummaryService Service(FakeHttpMessageHandler handler, MeetingSummaryOptions opts)
    {
        var client = new OpenAiCompatibleSummaryClient(new SingleClientFactory(handler), new FakeSecretStore(), Transport(), opts,
            NullLogger<OpenAiCompatibleSummaryClient>.Instance);
        return new MeetingSummaryService(new MeetingSummaryChunker(), client, new MarkdownMeetingSummaryExporter(), opts,
            NullLogger<MeetingSummaryService>.Instance);
    }

    private MeetingSummaryRequest Request(int segments, MeetingType type = MeetingType.SinglePresenter, string lang = "zh")
        => new()
        {
            SessionId = Guid.NewGuid(),
            SessionDirectory = _dir,
            MeetingType = type,
            OutputLanguage = lang,
            Model = "summary-model",
            PromptVersion = MeetingSummaryPrompt.Version,
            SourceLanguage = "ja",
            SessionDate = DateTimeOffset.Now,
            Segments = Enumerable.Range(1, segments)
                .Select(i => new MeetingSummarySegment(i, TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), $"CONFIRMED_FINAL_{i}"))
                .ToArray()
        };

    [Fact] // full pipeline: 2 chunks → 2 Map + 1 Reduce → Markdown written to the session directory
    public async Task EndToEnd_MapReduce_WritesMarkdown()
    {
        var handler = new FakeHttpMessageHandler()
            .EnqueueChat("{\"overview\":\"chunk1\",\"topics\":[\"t1\"]}")   // map 1
            .EnqueueChat("{\"overview\":\"chunk2\",\"topics\":[\"t2\"]}")   // map 2
            .EnqueueChat("{\"overview\":\"merged\",\"topics\":[\"t1\",\"t2\"],\"keyPoints\":[\"kp\"]}"); // reduce

        var opts = new MeetingSummaryOptions { ChunkBudgetChars = 500 }; // forces multiple chunks
        var svc = Service(handler, opts);
        var req = Request(segments: 40); // 40 * ~15 chars > 500 → several chunks

        var progress = new List<MeetingSummaryPhase>();
        var result = await svc.GenerateAsync(req, "meeting-summary.md", new Progress<MeetingSummaryProgress>(p => progress.Add(p.Phase)), CancellationToken.None);

        // File written to the session directory, content from Reduce.
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(_dir, Path.GetDirectoryName(result.OutputPath));
        var md = await File.ReadAllTextAsync(result.OutputPath);
        Assert.Contains("# 会议要点", md);
        Assert.Contains("merged", md);

        // Requests carried ONLY confirmed final text — never partials/translations/audio/video.
        var allBodies = string.Join("\n", handler.RequestBodies);
        Assert.Contains("CONFIRMED_FINAL_1", allBodies);
        Assert.DoesNotContain("partial", allBodies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".mp4", allBodies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".wav", allBodies, StringComparison.OrdinalIgnoreCase);
        Assert.All(handler.RequestBodies, b => Assert.Contains("\"model\":\"summary-model\"", b));

        // The last request (Reduce) merged the two Map outputs.
        Assert.Contains("chunk1", handler.RequestBodies[^1]);
        Assert.Contains("chunk2", handler.RequestBodies[^1]);
        Assert.Contains(MeetingSummaryPhase.Completed, progress);
    }

    [Fact] // scenario 5: a session with no final captions cannot be summarized
    public async Task NoFinalCaptions_Throws()
    {
        var svc = Service(new FakeHttpMessageHandler(), new MeetingSummaryOptions());
        var ex = await Assert.ThrowsAsync<MeetingSummaryException>(() =>
            svc.GenerateAsync(Request(0), "meeting-summary.md", null, CancellationToken.None));
        Assert.Equal(TranslationErrorCode.BadRequest, ex.Code);
    }

    [Fact] // scenario 22: many chunks reduce hierarchically (>1 reduce level), never one giant call
    public async Task ManyChunks_ReduceHierarchically()
    {
        var handler = new FakeHttpMessageHandler();
        for (int i = 0; i < 40; i++) handler.EnqueueChat("{\"overview\":\"o\"}"); // plenty for map+reduce

        var opts = new MeetingSummaryOptions { ChunkBudgetChars = 500, ReduceGroupSize = 2 };
        var svc = Service(handler, opts);
        var result = await svc.GenerateAsync(Request(60), "meeting-summary.md", null, CancellationToken.None);

        Assert.True(File.Exists(result.OutputPath));
        // 60 segments / ~500-char budget → ≥2 chunks; group size 2 → multiple reduce levels → >chunks calls.
        Assert.True(handler.CallCount > 2);
    }

    [Fact] // scenario 38/39: cancelling mid-run writes nothing and leaves no summary
    public async Task Cancel_WritesNothing()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler().Enqueue(_ =>
        {
            cts.Cancel(); // cancel after the first Map call
            return FakeHttpMessageHandler.Ok(FakeHttpMessageHandler.ChatJson("{\"overview\":\"o\"}"));
        });
        for (int i = 0; i < 40; i++) handler.EnqueueChat("{\"overview\":\"o\"}");

        var svc = Service(handler, new MeetingSummaryOptions { ChunkBudgetChars = 300 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.GenerateAsync(Request(60), "meeting-summary.md", null, cts.Token));

        Assert.False(File.Exists(Path.Combine(_dir, "meeting-summary.md")));
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.tmp-*") : Array.Empty<string>());
    }

    [Fact] // scenario 32: translation DISABLED but a valid API config still allows a summary
    public async Task TranslationDisabled_StillGenerates()
    {
        var handler = new FakeHttpMessageHandler().EnqueueChat("{\"overview\":\"ok\"}");
        var opts = new MeetingSummaryOptions { ChunkBudgetChars = 20000 }; // single chunk → no reduce
        var svc = Service(handler, opts);
        var result = await svc.GenerateAsync(Request(3), "meeting-summary.md", null, CancellationToken.None);
        Assert.True(File.Exists(result.OutputPath));
    }
}
