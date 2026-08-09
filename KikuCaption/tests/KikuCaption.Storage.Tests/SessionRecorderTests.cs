using System.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class SessionRecorderTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiku_rec_tests", Guid.NewGuid().ToString("N"));

    private (SessionRecorder Recorder, InMemoryStore Store, MeetingSession Session) Create(
        int upsertDelayMs = 0, bool failOnUpsert = false, double minGb = 0, int queueCapacity = 256)
    {
        Directory.CreateDirectory(_root);
        var store = new InMemoryStore { UpsertDelayMs = upsertDelayMs, FailOnUpsert = failOnUpsert };
        var exporter = new TranscriptExporter(store, "test-1.0");
        var options = new StorageOptions
        {
            OutputDirectory = _root, BaseDirectory = _root, MinimumFreeSpaceGb = minGb,
            ExportDebounceMs = 100, QueueCapacity = queueCapacity
        };
        var recorder = new SessionRecorder(store, exporter, options, NullLogger<SessionRecorder>.Instance);

        var id = Guid.NewGuid();
        var session = new MeetingSession
        {
            Id = id,
            StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = "zh",
            OutputDirectory = SessionPaths.BuildSessionDirectory(_root, new MeetingSession
            {
                Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "zh", OutputDirectory = _root
            })
        };
        return (recorder, store, session);
    }

    private static TranscriptSegment Final(Guid sid, string text, Guid? id = null)
        => StorageTestContext.Final(sid, text, id: id);

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
        }
    }

    [Fact] // 1: final persisted to store while running
    public async Task Final_PersistedImmediately()
    {
        var (recorder, store, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        await recorder.RecordFinalAsync(Final(session.Id, "你好世界"));
        await WaitAsync(() => recorder.SavedFinalCount >= 1);

        var segs = await store.GetSegmentsAsync(session.Id, CancellationToken.None);
        Assert.Single(segs);
        await recorder.StopSessionAsync(DateTimeOffset.Now);
    }

    [Fact] // 2: partial not persisted
    public async Task Partial_NotPersisted()
    {
        var (recorder, store, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        await recorder.RecordFinalAsync(Final(session.Id, "临时") with { Status = TranscriptStatus.Partial });
        await Task.Delay(200);

        Assert.Empty(await store.GetSegmentsAsync(session.Id, CancellationToken.None));
        Assert.Equal(0, recorder.SavedFinalCount);
        await recorder.StopSessionAsync(DateTimeOffset.Now);
    }

    [Fact] // 3: duplicate final deduped
    public async Task DuplicateFinal_Deduped()
    {
        var (recorder, store, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        var id = Guid.NewGuid();
        await recorder.RecordFinalAsync(Final(session.Id, "一", id));
        await recorder.RecordFinalAsync(Final(session.Id, "一", id));
        await WaitAsync(() => recorder.SavedFinalCount >= 1);
        await Task.Delay(150);

        Assert.Single(await store.GetSegmentsAsync(session.Id, CancellationToken.None));
        await recorder.StopSessionAsync(DateTimeOffset.Now);
    }

    [Fact] // 4 + 9: stop drains queue; last final not lost
    public async Task Stop_DrainsAllFinals()
    {
        var (recorder, store, session) = Create(upsertDelayMs: 10);
        await recorder.StartSessionAsync(session, CancellationToken.None);
        for (int i = 0; i < 10; i++)
        {
            await recorder.RecordFinalAsync(Final(session.Id, $"第{i}句"));
        }

        await recorder.StopSessionAsync(DateTimeOffset.Now);
        Assert.Equal(10, (await store.GetSegmentsAsync(session.Id, CancellationToken.None)).Count);
    }

    [Fact] // 7: queue full applies back-pressure, never drops
    public async Task QueueFull_BackPressure_NoDrop()
    {
        var (recorder, store, session) = Create(upsertDelayMs: 15, queueCapacity: 2);
        await recorder.StartSessionAsync(session, CancellationToken.None);
        for (int i = 0; i < 20; i++)
        {
            await recorder.RecordFinalAsync(Final(session.Id, $"句{i}")); // blocks when full
        }

        await recorder.StopSessionAsync(DateTimeOffset.Now);
        Assert.Equal(20, (await store.GetSegmentsAsync(session.Id, CancellationToken.None)).Count);
    }

    [Fact] // 6: write failure is surfaced, not faked
    public async Task WriteFailure_RaisesEvent_AndStopsAccepting()
    {
        var (recorder, _, session) = Create(failOnUpsert: true);
        var failed = false;
        recorder.StorageFailed += (_, _) => failed = true;

        await recorder.StartSessionAsync(session, CancellationToken.None);
        await recorder.RecordFinalAsync(Final(session.Id, "会失败"));
        await WaitAsync(() => failed);

        Assert.True(failed);
        Assert.NotNull(recorder.StorageError);
        await Assert.ThrowsAsync<StorageException>(() => recorder.RecordFinalAsync(Final(session.Id, "之后")));
        await recorder.StopSessionAsync(DateTimeOffset.Now);
    }

    [Fact] // 8: RecordFinalAsync returns promptly (does not block caller on disk I/O)
    public async Task RecordFinal_DoesNotBlockCaller()
    {
        var (recorder, _, session) = Create(upsertDelayMs: 200);
        await recorder.StartSessionAsync(session, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await recorder.RecordFinalAsync(Final(session.Id, "快"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 150, $"RecordFinal blocked {sw.ElapsedMilliseconds}ms");
        await recorder.StopSessionAsync(DateTimeOffset.Now);
    }

    [Fact] // M5: RecordingPath persisted and reflected in session.json
    public async Task SetRecordingPath_UpdatesSessionJson()
    {
        var (recorder, _, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        var mp4 = Path.Combine(session.OutputDirectory, "meeting.mp4");
        await recorder.SetRecordingPathAsync(mp4);
        await recorder.StopSessionAsync(DateTimeOffset.Now);

        using var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(session.OutputDirectory, "session.json")));
        Assert.Equal(mp4, json.RootElement.GetProperty("recordingPath").GetString());
    }

    [Fact] // 5: files are exported (drain + final export) after stop
    public async Task Stop_ExportsFiles()
    {
        var (recorder, _, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        await recorder.RecordFinalAsync(Final(session.Id, "会议内容"));
        await recorder.StopSessionAsync(DateTimeOffset.Now);

        Assert.True(File.Exists(Path.Combine(session.OutputDirectory, "transcript.srt")));
        Assert.Contains("会议内容", File.ReadAllText(Path.Combine(session.OutputDirectory, "transcript.srt")));
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
