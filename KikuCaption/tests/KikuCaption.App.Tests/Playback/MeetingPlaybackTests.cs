using System.IO;
using System.Xml.Linq;
using KikuCaption.App.Playback;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Sqlite;
using LibVLCSharp.Shared;
using Xunit;

namespace KikuCaption.App.Tests.Playback;

public sealed class MeetingPlaybackTests
{
    [Fact]
    public void PlaybackWindow_ReadOnlyTextBindings_AreOneWay()
    {
        var projectDirectory = FindProjectDirectory();
        var xaml = XDocument.Load(Path.Combine(projectDirectory, "Views", "MeetingPlaybackWindow.xaml"));
        var bindings = xaml.Descendants()
            .Attributes("Text")
            .Select(x => x.Value)
            .Where(x => x.Contains("Binding PositionText", StringComparison.Ordinal) ||
                        x.Contains("Binding DurationText", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, bindings.Length);
        Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding, StringComparison.Ordinal));
    }

    [Fact]
    public void LibVlcNativeRuntime_LoadsWithoutInstalledVlc()
    {
        LibVlcRuntimeLocator.Initialize();
        LibVlcRuntimeLocator.Initialize();
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
    public void CaptionOffset_AppliesToSeekAndClampsToTenSeconds()
    {
        var vm = new MeetingPlaybackViewModel(Session(new[] { Segment(1, 12.345, 14) }));
        vm.CaptionOffsetSeconds = 2.5;
        Assert.Equal(TimeSpan.FromSeconds(14.845), vm.SeekTarget(vm.Captions[0]));
        vm.AdjustCaptionOffset(100);
        Assert.Equal(10, vm.CaptionOffsetSeconds);
    }

    [Fact]
    public void CaptionOffset_AppliesToAutomaticHighlight()
    {
        var vm = new MeetingPlaybackViewModel(Session(new[]
        {
            Segment(1, 2, 4), Segment(2, 5, 8)
        })) { CaptionOffsetSeconds = 2 };

        vm.UpdateActiveCaption(TimeSpan.FromSeconds(6));

        Assert.Same(vm.Captions[0], vm.ActiveCaption);
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

    [Fact]
    public async Task Coordinator_RecoversRecordingAfterOutputRootWasMoved()
    {
        var currentRoot = Path.Combine(Path.GetTempPath(), "kiku_playback_moved", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var startedAt = DateTimeOffset.Now;
        var session = new MeetingSession
        {
            Id = id,
            StartedAt = startedAt,
            RecognitionLanguage = "ja",
            OutputDirectory = @"C:\old-user\old-repository\Meetings\old-session",
            RecordingPath = @"C:\old-user\old-repository\Meetings\old-session\meeting.mp4"
        };
        var relocatedDirectory = SessionPaths.BuildSessionDirectory(currentRoot, session);
        Directory.CreateDirectory(relocatedDirectory);
        var relocatedMedia = Path.Combine(relocatedDirectory, "meeting.mp4");
        await File.WriteAllBytesAsync(relocatedMedia, new byte[] { 0 });

        try
        {
            var store = new TestTranscriptStore
            {
                Session = new StoredSession(session, "Completed", 0),
                Segments = Array.Empty<StoredSegment>()
            };
            var options = new StorageOptions { OutputDirectory = currentRoot };

            var result = await new MeetingPlaybackCoordinator(store, options)
                .LoadAsync(id, CancellationToken.None);

            Assert.Equal(Path.GetFullPath(relocatedMedia), result.MediaPath);
        }
        finally
        {
            if (Directory.Exists(currentRoot)) Directory.Delete(currentRoot, true);
        }
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

    private static string FindProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "KikuCaption.App");
            if (File.Exists(Path.Combine(candidate, "KikuCaption.App.csproj"))) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("KikuCaption.App project directory was not found.");
    }
}
