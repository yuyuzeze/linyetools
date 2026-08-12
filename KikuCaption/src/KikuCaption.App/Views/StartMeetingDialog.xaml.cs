using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using KikuCaption.App.ViewModels;
using KikuCaption.Audio.Capture;

namespace KikuCaption.App.Views;

/// <summary>
/// Compact "start meeting" dialog (UI-R2, extended in UI-R5A with audio inputs). It edits an
/// independent draft; the caller applies the draft to the live meeting state only when this returns
/// true (via <see cref="MeetingStartCoordinator"/>). Cancel / Esc / window-close all resolve to a
/// non-true result, so the main view model is untouched. Code-behind is limited to view concerns:
/// setting the confirm result and driving the live microphone level meter (a UI-only, best-effort
/// device probe that is fully torn down when the dialog closes).
/// </summary>
public partial class StartMeetingDialog : Window
{
    private readonly StartMeetingDialogViewModel _draft;
    private readonly MicrophoneLevelMeter? _meter;
    private readonly DispatcherTimer? _levelTimer;

    public StartMeetingDialog(StartMeetingDialogViewModel draft, MicrophoneLevelMeter? meter = null)
    {
        InitializeComponent();
        _draft = draft;
        _meter = meter;
        DataContext = draft;

        if (_meter is not null)
        {
            _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _levelTimer.Tick += (_, _) =>
            {
                _draft.InputLevel = _meter.CurrentLevel;
                _draft.MicrophoneUnavailable = _draft.RecordMicrophone && !_meter.IsAvailable;
            };

            _draft.PropertyChanged += OnDraftChanged;
            Loaded += (_, _) => SyncMeter();
            Closed += (_, _) => TeardownMeter();
        }
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StartMeetingDialogViewModel.RecordMicrophone)
            or nameof(StartMeetingDialogViewModel.SelectedMicDeviceId))
        {
            SyncMeter();
        }
    }

    // Start/stop the meter to match the current mic choice; restart on a device change.
    private void SyncMeter()
    {
        if (_meter is null || _levelTimer is null)
        {
            return;
        }

        if (_draft.RecordMicrophone)
        {
            bool available = _meter.Start(_draft.SelectedMicDeviceId);
            _draft.MicrophoneUnavailable = !available;
            _levelTimer.Start();
        }
        else
        {
            _levelTimer.Stop();
            _meter.Stop();
            _draft.InputLevel = 0;
            _draft.MicrophoneUnavailable = false;
        }
    }

    private void TeardownMeter()
    {
        _draft.PropertyChanged -= OnDraftChanged;
        _levelTimer?.Stop();
        _meter?.Dispose();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StartMeetingDialogViewModel draft && draft.CanStart)
        {
            DialogResult = true;
        }
    }
}
