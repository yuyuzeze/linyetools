using System.Diagnostics;
using System.Text.Json;
using KikuCaption.Audio.Wav;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using KikuCaption.Storage;
using KikuCaption.Storage.Export;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// Full real-model end-to-end for Milestone 4: real pipeline → SessionRecorder → real SQLite +
/// files, using a synthesized Chinese WAV. Gated by KIKU_REALMODEL=1 and KIKU_ZH_WAV.
/// </summary>
[Trait("Category", "RealModel")]
public class RealtimeStorageIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RealtimeStorageIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealPipeline_PersistsChineseSession_ToSqliteAndFiles()
    {
        if (!RealModelSupport.Enabled) { _output.WriteLine("[SKIPPED] KIKU_REALMODEL!=1"); return; }
        var wav = RealModelSupport.ChineseWav;
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav)) { _output.WriteLine("[SKIPPED] 无 KIKU_ZH_WAV"); return; }
        var located = RealModelSupport.Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir)) { _output.WriteLine("[SKIPPED] 无 venv/模型"); return; }

        var root = Path.Combine(Path.GetTempPath(), "kiku_m4_e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new StorageOptions { OutputDirectory = root, BaseDirectory = root, MinimumFreeSpaceGb = 0, ExportDebounceMs = 300 };

        await using var repo = new SqliteTranscriptRepository(options.ResolveDatabasePath(), NullLogger<SqliteTranscriptRepository>.Instance);
        var exporter = new TranscriptExporter(repo, "itest-1.0");
        await using var recorder = new SessionRecorder(repo, exporter, options, NullLogger<SessionRecorder>.Instance);

        await using var pipeline = new RealtimeCaptionPipeline(
            RealModelSupport.RecognizerFactory(located.Value.Options),
            new ProgressiveCaptionOptions { PartialIntervalMs = 700, SilenceFinalMs = 600, MaxSentenceSeconds = 12, MaxWaitSeconds = 20 },
            new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }), NullLogger<RealtimeCaptionPipeline>.Instance);

        await pipeline.StartAsync(WavFileAudioReader.ReadAsync(wav!), "zh", CancellationToken.None);

        var seed = new MeetingSession { Id = pipeline.SessionId, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "zh", OutputDirectory = root };
        var session = seed with { OutputDirectory = SessionPaths.BuildSessionDirectory(root, seed) };
        await recorder.StartSessionAsync(session, CancellationToken.None);

        pipeline.FinalProduced += OnFinal;

        async void OnFinal(object? _, CaptionFinalEventArgs e)
        {
            try
            {
                await recorder.RecordFinalAsync(new TranscriptSegment
                {
                    Id = Guid.NewGuid(),
                    SessionId = recorder.SessionId,
                    StartTime = e.StartTime,
                    EndTime = e.EndTime,
                    Language = "zh",
                    Text = e.Text,
                    Status = TranscriptStatus.Final,
                    CreatedAt = DateTimeOffset.Now
                });
            }
            catch { /* surfaced via recorder.StorageError */ }
        }

        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(90));

        // Mid-session (before StopSession): finals must already be in SQLite (immediate persistence).
        var sw = Stopwatch.StartNew();
        while (recorder.SavedFinalCount == 0 && sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50);
        }

        var midSegments = await repo.GetSegmentsAsync(session.Id, CancellationToken.None);
        _output.WriteLine($"mid-session SQLite segments: {midSegments.Count}");
        Assert.NotEmpty(midSegments);
        Assert.Contains(midSegments, s => s.Segment.Text.Any(c => c >= '一' && c <= '鿿'));

        await recorder.StopSessionAsync(DateTimeOffset.Now);

        // Files exist and are valid.
        var dir = session.OutputDirectory;
        foreach (var name in new[] { "transcript.json", "transcript.txt", "transcript.srt", "session.json" })
        {
            var path = Path.Combine(dir, name);
            Assert.True(File.Exists(path), $"missing {name}");
            _output.WriteLine($"{name}: {new FileInfo(path).Length} bytes");
        }

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "transcript.json")));
        Assert.True(json.RootElement.GetArrayLength() > 0);
        var srt = File.ReadAllText(Path.Combine(dir, "transcript.srt"));
        _output.WriteLine("SRT:\n" + srt);
        Assert.Contains(srt, c => c >= '一' && c <= '鿿');

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        try { Directory.Delete(root, true); } catch { }
    }
}
