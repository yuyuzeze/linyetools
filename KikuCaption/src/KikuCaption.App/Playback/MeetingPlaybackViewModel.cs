using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.Playback;

public sealed partial class MeetingPlaybackViewModel : ObservableObject
{
    private int _activeIndex = -1;

    public MeetingPlaybackViewModel(MeetingPlaybackSession session)
    {
        Session = session;
        Captions = new(session.Captions.Select(x => new PlaybackCaptionViewModel(x)));
    }

    public MeetingPlaybackSession Session { get; }
    public ObservableCollection<PlaybackCaptionViewModel> Captions { get; }
    public string TitleText => Session.Session.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private long _positionMilliseconds;
    [ObservableProperty] private long _durationMilliseconds;
    [ObservableProperty] private PlaybackCaptionViewModel? _activeCaption;
    [ObservableProperty] private float _playbackRate = 1f;

    public string PositionText => Format(PositionMilliseconds);
    public string DurationText => Format(DurationMilliseconds);

    partial void OnPositionMillisecondsChanged(long value)
    {
        OnPropertyChanged(nameof(PositionText));
        UpdateActiveCaption(TimeSpan.FromMilliseconds(Math.Max(0, value)));
    }

    partial void OnDurationMillisecondsChanged(long value) => OnPropertyChanged(nameof(DurationText));
    public TimeSpan SeekTarget(PlaybackCaptionViewModel caption) => caption.Segment.StartTime;

    public void UpdateActiveCaption(TimeSpan position)
    {
        var index = FindCaption(position);
        if (index == _activeIndex) return;
        if (_activeIndex >= 0 && _activeIndex < Captions.Count) Captions[_activeIndex].IsActive = false;
        _activeIndex = index;
        ActiveCaption = index >= 0 ? Captions[index] : null;
        if (ActiveCaption is not null) ActiveCaption.IsActive = true;
    }

    private int FindCaption(TimeSpan position)
    {
        int lo = 0, hi = Captions.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (Captions[mid].Segment.StartTime <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }
}
