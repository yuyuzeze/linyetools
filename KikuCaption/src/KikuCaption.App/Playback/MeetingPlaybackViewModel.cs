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
    [ObservableProperty] private double _captionOffsetSeconds;

    public string PositionText => Format(PositionMilliseconds);
    public string DurationText => Format(DurationMilliseconds);
    public string CaptionOffsetText => $"{CaptionOffsetSeconds:+0.0;-0.0;0.0} s";

    partial void OnPositionMillisecondsChanged(long value)
    {
        OnPropertyChanged(nameof(PositionText));
        UpdateActiveCaption(TimeSpan.FromMilliseconds(Math.Max(0, value)));
    }

    partial void OnDurationMillisecondsChanged(long value) => OnPropertyChanged(nameof(DurationText));

    partial void OnCaptionOffsetSecondsChanged(double value)
    {
        var clamped = Math.Clamp(value, -10d, 10d);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            CaptionOffsetSeconds = clamped;
            return;
        }
        OnPropertyChanged(nameof(CaptionOffsetText));
        UpdateActiveCaption(TimeSpan.FromMilliseconds(Math.Max(0, PositionMilliseconds)), force: true);
    }

    public TimeSpan SeekTarget(PlaybackCaptionViewModel caption)
        => MaxZero(caption.Segment.StartTime + TimeSpan.FromSeconds(CaptionOffsetSeconds));

    public void AdjustCaptionOffset(double seconds)
        => CaptionOffsetSeconds = Math.Round(Math.Clamp(CaptionOffsetSeconds + seconds, -10d, 10d), 1);

    public void UpdateActiveCaption(TimeSpan position, bool force = false)
    {
        var index = FindCaption(position);
        if (!force && index == _activeIndex) return;
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
            var effectiveStart = Captions[mid].Segment.StartTime + TimeSpan.FromSeconds(CaptionOffsetSeconds);
            if (effectiveStart <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
