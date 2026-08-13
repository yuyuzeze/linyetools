using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KikuCaption.App.Localization;
using KikuCaption.App.Playback;
using LibVLCSharp.Shared;

namespace KikuCaption.App.Views;

public partial class MeetingPlaybackWindow : Window
{
    private readonly MeetingPlaybackViewModel _viewModel;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly Media _media;
    private readonly DispatcherTimer _timer;
    private bool _playbackStarted;
    private bool _disposed;

    public MeetingPlaybackWindow(MeetingPlaybackSession session)
    {
        InitializeComponent();
        _viewModel = new MeetingPlaybackViewModel(session);
        DataContext = _viewModel;

        LibVlcRuntimeLocator.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        _player = new MediaPlayer(_libVlc);
        _media = new Media(_libVlc, new Uri(session.MediaPath));
        VideoView.MediaPlayer = _player;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += Timer_Tick;
        _player.LengthChanged += (_, e) => Dispatcher.Invoke(() => _viewModel.DurationMilliseconds = e.Length);
        _player.Playing += (_, _) => Dispatcher.Invoke(() => SetPlaying(true));
        _player.Paused += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _player.Stopped += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _player.EndReached += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        ContentRendered += PlaybackWindow_ContentRendered;
        Closed += (_, _) => DisposePlayer();
    }

    private void PlaybackWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_playbackStarted || _disposed) return;

        // LibVLC needs VideoView's native window handle before playback starts.
        // Starting in the constructor can produce audio without a visible video surface.
        _playbackStarted = true;
        _player.Play(_media);
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _viewModel.PositionMilliseconds = Math.Max(0, _player.Time);
        if (_player.Length > 0) _viewModel.DurationMilliseconds = _player.Length;
        if (_viewModel.ActiveCaption is { } active)
            CaptionList.ScrollIntoView(active);
    }

    private void SetPlaying(bool playing)
    {
        _viewModel.IsPlaying = playing;
        PlayPauseButton.Content = LocalizationService.Instance[playing ? "Playback.Pause" : "Playback.Play"];
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _viewModel.PositionMilliseconds = 0;
    }

    private void Caption_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaybackCaptionViewModel caption) return;
        var target = _viewModel.SeekTarget(caption);
        _player.Time = (long)target.TotalMilliseconds;
        _viewModel.PositionMilliseconds = _player.Time;
        if (!_player.IsPlaying) _player.Play();
    }

    private void PositionSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _player.Time = (long)PositionSlider.Value;
        _viewModel.PositionMilliseconds = _player.Time;
    }

    private void RateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || RateBox.SelectedItem is not ComboBoxItem item) return;
        if (float.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            _player.SetRate(rate);
            _viewModel.PlaybackRate = rate;
        }
    }

    private void DisposePlayer()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        VideoView.MediaPlayer = null;
        _player.Stop();
        _media.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
    }
}
