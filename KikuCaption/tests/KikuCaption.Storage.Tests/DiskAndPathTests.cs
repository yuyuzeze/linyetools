using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class DiskAndPathTests
{
    [Fact] // 1: sufficient space reported
    public void DiskSpace_ReportsFreeSpace()
    {
        var temp = Path.GetTempPath();
        Assert.True(DiskSpace.HasAtLeastGb(temp, 0));
        Assert.True(DiskSpace.GetFreeGb(temp) > 0);
    }

    [Fact] // 2: start refused when free space below minimum
    public async Task StartSession_InsufficientDisk_Refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiku_disk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new InMemoryStore();
        var exporter = new TranscriptExporter(store, "t");
        var options = new StorageOptions { OutputDirectory = root, BaseDirectory = root, MinimumFreeSpaceGb = 1_000_000 };
        var recorder = new SessionRecorder(store, exporter, options, NullLogger<SessionRecorder>.Instance);

        var session = NewSession(root);
        var ex = await Assert.ThrowsAsync<StorageException>(() => recorder.StartSessionAsync(session, CancellationToken.None));
        Assert.Equal("insufficient_disk", ex.Code);
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact] // 5: path traversal rejected
    public void PathTraversal_Rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiku_root");
        var outside = Path.Combine(root, "..", "evil");
        Assert.Throws<StorageException>(() => SessionPaths.EnsureWithinRoot(root, outside));

        // A normally-built session directory stays inside the root.
        var session = NewSession(root);
        SessionPaths.EnsureWithinRoot(root, session.OutputDirectory); // no throw
    }

    [Fact] // 4/5: start refused when output directory escapes the root
    public async Task StartSession_OutputOutsideRoot_Rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiku_disk2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new InMemoryStore();
        var exporter = new TranscriptExporter(store, "t");
        var options = new StorageOptions { OutputDirectory = root, BaseDirectory = root, MinimumFreeSpaceGb = 0 };
        var recorder = new SessionRecorder(store, exporter, options, NullLogger<SessionRecorder>.Instance);

        var session = new MeetingSession
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = "zh",
            OutputDirectory = Path.Combine(Path.GetTempPath(), "kiku_outside", Guid.NewGuid().ToString("N"))
        };

        var ex = await Assert.ThrowsAsync<StorageException>(() => recorder.StartSessionAsync(session, CancellationToken.None));
        Assert.Equal("path_traversal", ex.Code);
        try { Directory.Delete(root, true); } catch { }
    }

    private static MeetingSession NewSession(string root)
    {
        var id = Guid.NewGuid();
        var seed = new MeetingSession { Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "zh", OutputDirectory = root };
        return seed with { OutputDirectory = SessionPaths.BuildSessionDirectory(root, seed) };
    }
}
