using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Export;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

/// <summary>
/// UI-R6A integration: a recording-only meeting drives the SAME SessionRecorder + store path as a
/// captioned meeting, but produces zero finals. It must still persist the session, save the MP4
/// recording path, appear in history with a 0 caption count, create no translation jobs, and export
/// without error. Uses the fake in-memory store + the real recorder/exporter — no live devices.
/// </summary>
public class RecordingOnlySessionTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiku_reconly", Guid.NewGuid().ToString("N"));

    private (SessionRecorder Recorder, InMemoryStore Store, MeetingSession Session) Create()
    {
        Directory.CreateDirectory(_root);
        var store = new InMemoryStore();
        var exporter = new TranscriptExporter(store, "test-1.0");
        var options = new StorageOptions
        {
            OutputDirectory = _root, BaseDirectory = _root, MinimumFreeSpaceGb = 0,
            ExportDebounceMs = 100, QueueCapacity = 256
        };
        var recorder = new SessionRecorder(store, exporter, options, NullLogger<SessionRecorder>.Instance);

        var id = Guid.NewGuid();
        var session = new MeetingSession
        {
            Id = id,
            StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = "ja",
            // Recording-only meetings never enable translation (capability snapshot gates it off).
            TranslationEnabled = false,
            OutputDirectory = SessionPaths.BuildSessionDirectory(_root, new MeetingSession
            {
                Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "ja", OutputDirectory = _root
            })
        };
        return (recorder, store, session);
    }

    [Fact] // R6A: recording-only Start→Stop persists the session + MP4 path, 0 captions, 0 jobs
    public async Task RecordingOnly_PersistsSession_NoCaptions_NoJobs()
    {
        var (recorder, store, session) = Create();

        await recorder.StartSessionAsync(session, CancellationToken.None);
        var mp4 = Path.Combine(session.OutputDirectory, "meeting.mp4");
        await File.WriteAllBytesAsync(mp4, new byte[] { 0, 1, 2, 3 }); // stand-in for the FFmpeg output
        await recorder.SetRecordingPathAsync(mp4);

        // NO RecordFinalAsync calls at all — this is the whole point of recording-only mode.
        await recorder.StopSessionAsync(DateTimeOffset.Now);

        // Session written to the store and completed.
        var stored = await store.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(SessionStates.Completed, stored!.State);

        // MP4 recording path saved (DB row + session.json).
        Assert.Equal(mp4, stored.Session.RecordingPath);
        using var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(session.OutputDirectory, "session.json")));
        Assert.Equal(mp4, json.RootElement.GetProperty("recordingPath").GetString());

        // FinalCount == 0 and the recorder never counted a final.
        Assert.Equal(0, recorder.SavedFinalCount);
        Assert.Empty(await store.GetSegmentsAsync(session.Id, CancellationToken.None));

        // No translation jobs were created.
        Assert.Empty(await store.GetJobsForSessionAsync(session.Id, CancellationToken.None));
    }

    [Fact] // R6A: history shows the recording-only meeting with a recording present but 0 captions
    public async Task RecordingOnly_AppearsInHistory_WithRecordingAndZeroCaptions()
    {
        var (recorder, store, session) = Create();
        await recorder.StartSessionAsync(session, CancellationToken.None);
        var mp4 = Path.Combine(session.OutputDirectory, "meeting.mp4");
        await recorder.SetRecordingPathAsync(mp4);
        await recorder.StopSessionAsync(DateTimeOffset.Now);

        var recent = await store.GetRecentSessionsAsync(20, CancellationToken.None);
        var row = Assert.Single(recent, s => s.Session.Id == session.Id);

        Assert.Equal(0, row.SegmentCount);                         // "0 条字幕"
        Assert.False(string.IsNullOrEmpty(row.Session.RecordingPath)); // "有录屏"
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
