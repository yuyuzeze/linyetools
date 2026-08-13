using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.Core.Models;

namespace KikuCaption.App.Playback;

public sealed record MeetingPlaybackSession(MeetingSession Session, string MediaPath,
    IReadOnlyList<TranscriptSegment> Captions);

public sealed partial class PlaybackCaptionViewModel : ObservableObject
{
    public PlaybackCaptionViewModel(TranscriptSegment segment)
    {
        Segment = segment;
        TimeText = segment.StartTime.TotalHours >= 1
            ? segment.StartTime.ToString(@"h\:mm\:ss")
            : segment.StartTime.ToString(@"mm\:ss");
    }

    public TranscriptSegment Segment { get; }
    public string TimeText { get; }
    public string Text => Segment.Text;
    public string? Translation => Segment.Translation;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    [ObservableProperty] private bool _isActive;
}
