using System.IO;
using KikuCaption.App.Playback;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using LibVLCSharp.Shared;
using Xunit;

namespace KikuCaption.App.Tests.Playback;

public sealed class MeetingPlaybackTests
{
    [Fact]
    public void LibVlcNativeRuntime_LoadsWithoutInstalledVlc()
    {
        LibVLCSharp.Shared.Core.Initialize();
        using var engine = new LibVLC("--no-video-title-show");
        Assert.NotNull(engine);
    }

    [Fact]
    public void CaptionClick_UsesExactPersistedStartTime()
    {
        var session = Session(new[] { Segment(1, 12.345, 14), Segment(2, 18, 20) });
        var vm = new MeetingPlaybackViewModel(session);
        Assert.Equal(TimeSpan.FromSeconds(12.345), vm.SeekTarget(vm.Captions[0]));
    }

    [Fact]
    public void Position_HighlightsLatestCaptionAtOrBeforeTime()
    {
        var vm = new MeetingPlaybackViewModel(Session(new[]
        {
            Segment(1, 2, 4), Segment(2, 5, 8), Segment(3, 10, 12)
        }));
        vm.UpdateActiveCaption(TimeSpan.FromSeconds(6));
        Assert.Same(vm.Captions[1], vm.ActiveCaption);
        Assert.True(vm.Captions[1].IsActive);
        Assert.False(vm.Captions[0].IsActive);
    }

    [Fact]
    public async Task Coordinator_LoadsFinalsInSequenceAndResolvesMedia()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiku_playback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var media = Path.Combine(root, "meeting.mp4");
        await File.WriteAllBytesAsync(media, new byte[] { 0 });
        try
        {
            var id = Guid.NewGuid();
            var store = new TestTranscriptStore
            {
                Session = new StoredSession(new MeetingSession
                {
                    Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "ja",
                    OutputDirectory = root, RecordingPath = "meeting.mp4"
                }, "Completed", 3),
                Segments = new[]
                {
                    new StoredSegment(Segment(3, 8, 9) with { SessionId = id }, 3),
                    new StoredSegment(Segment(1, 1, 2) with { SessionId = id }, 1),
                    new StoredSegment(Segment(2, 4, 5) with { SessionId = id, Status = TranscriptStatus.Partial }, 2)
                }
            };
            var result = await new MeetingPlaybackCoordinator(store).LoadAsync(id, CancellationToken.None);
            Assert.Equal(Path.GetFullPath(media), result.MediaPath);
            Assert.Equal(new[] { "caption-1", "caption-3" }, result.Captions.Select(x => x.Text));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Coordinator_RejectsMissingRecordingWithoutReadingCaptions()
    {
        var id = Guid.NewGuid();
        var store = new TestTranscriptStore
        {
            Session = new StoredSession(new MeetingSession
            {
                Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "ja",
                OutputDirectory = Path.GetTempPath(), RecordingPath = null
            }, "Completed", 1)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MeetingPlaybackCoordinator(store).LoadAsync(id, CancellationToken.None));
    }

    private static MeetingPlaybackSession Session(IEnumerable<TranscriptSegment> segments)
        => new(new MeetingSession
        {
            Id = Guid.NewGuid(), StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = "ja", OutputDirectory = Path.GetTempPath()
        }, "meeting.mp4", segments.ToArray());

    private static TranscriptSegment Segment(int n, double start, double end) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.Empty,
        StartTime = TimeSpan.FromSeconds(start), EndTime = TimeSpan.FromSeconds(end),
        Language = "ja", Text = $"caption-{n}", Status = TranscriptStatus.Final,
        CreatedAt = DateTimeOffset.Now
    };
}
