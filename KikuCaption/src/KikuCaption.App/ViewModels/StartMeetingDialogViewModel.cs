using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.Audio.Diagnostics;
using KikuCaption.Recording.CaptureTargets;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Draft view model for the "start meeting" dialog (UI-R2 dialog-draft fix, extended in UI-R5A with
/// audio inputs). It is fully independent of the live <see cref="RealtimeCaptionViewModel"/>: it
/// copies the current capture target + audio choice in as initial values, and all edits stay in this
/// draft. Nothing is written back to the main view model here — the caller applies the draft once, on
/// confirm, via <see cref="MeetingStartCoordinator"/>. Cancel / Esc / close therefore change nothing.
/// </summary>
public sealed partial class StartMeetingDialogViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<AudioCaptureDeviceInfo>>? _refreshMicDevices;

    public StartMeetingDialogViewModel(
        MeetingCaptureTarget initial,
        IEnumerable<string> windows,
        string outputRoot,
        MeetingAudioOptions? audio = null,
        IReadOnlyList<AudioCaptureDeviceInfo>? micDevices = null,
        Func<IReadOnlyList<AudioCaptureDeviceInfo>>? refreshMicDevices = null)
    {
        _captureType = initial.CaptureType;
        _selectedWindow = initial.WindowTitle;
        OutputRoot = outputRoot;

        var a = audio ?? MeetingAudioOptions.Default;
        _recordSystemAudio = a.RecordSystemAudio;
        _recordMicrophone = a.RecordMicrophone;
        _selectedMicDeviceId = a.MicrophoneDeviceId;
        _refreshMicDevices = refreshMicDevices;

        foreach (var window in windows)
        {
            Windows.Add(window);
        }

        // If seeded with a window that is no longer listed, keep it visible so the choice is retained.
        if (!string.IsNullOrWhiteSpace(_selectedWindow) && !Windows.Contains(_selectedWindow))
        {
            Windows.Insert(0, _selectedWindow!);
        }

        PopulateMicDevices(micDevices ?? Array.Empty<AudioCaptureDeviceInfo>());
    }

    // ---- capture target -------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowCapture))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _captureType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string? _selectedWindow;

    public ObservableCollection<string> Windows { get; } = new();

    public string OutputRoot { get; }

    public bool IsWindowCapture => string.Equals(CaptureType, MeetingCaptureTarget.Window, StringComparison.OrdinalIgnoreCase);

    // ---- audio inputs (UI-R5A) ------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyInput))]
    [NotifyPropertyChangedFor(nameof(ShowNoInputWarning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _recordSystemAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyInput))]
    [NotifyPropertyChangedFor(nameof(ShowNoInputWarning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _recordMicrophone;

    [ObservableProperty] private string? _selectedMicDeviceId;

    /// <summary>Live input level [0,1] of the selected microphone; set by the view's meter timer.</summary>
    [ObservableProperty] private double _inputLevel;

    /// <summary>False when the microphone is enabled but its device cannot be opened (non-silent).</summary>
    [ObservableProperty] private bool _microphoneUnavailable;

    public ObservableCollection<AudioCaptureDeviceInfo> MicDevices { get; } = new();

    /// <summary>At least one audio input must be selected to start.</summary>
    public bool HasAnyInput => RecordSystemAudio || RecordMicrophone;

    /// <summary>Show the "select an input" warning when neither system audio nor microphone is chosen.</summary>
    public bool ShowNoInputWarning => !HasAnyInput;

    // ---- start gating ---------------------------------------------------

    /// <summary>True when the draft is a valid, startable target with at least one audio input.</summary>
    public bool CanStart => ToTarget().IsValid && HasAnyInput;

    /// <summary>Snapshots the draft into an immutable target (window title dropped for screen capture).</summary>
    public MeetingCaptureTarget ToTarget()
        => new(CaptureType, IsWindowCapture ? SelectedWindow : null);

    /// <summary>Snapshots the audio-input choice.</summary>
    public MeetingAudioOptions ToAudioOptions()
        => new(RecordSystemAudio, RecordMicrophone, RecordMicrophone ? SelectedMicDeviceId : null);

    [RelayCommand]
    private void RefreshWindows()
    {
        var previous = SelectedWindow;
        Windows.Clear();
        foreach (var window in WindowEnumerator.EnumerateWindows())
        {
            Windows.Add(window.Title);
        }

        if (!string.IsNullOrWhiteSpace(previous) && !Windows.Contains(previous))
        {
            Windows.Insert(0, previous!);
        }
        SelectedWindow = previous;
    }

    [RelayCommand]
    private void RefreshMicDevices()
    {
        if (_refreshMicDevices is null)
        {
            return;
        }

        PopulateMicDevices(_refreshMicDevices());
    }

    private void PopulateMicDevices(IReadOnlyList<AudioCaptureDeviceInfo> devices)
    {
        var previous = SelectedMicDeviceId;
        MicDevices.Clear();
        foreach (var d in devices)
        {
            MicDevices.Add(d);
        }

        // Keep the saved selection if still present; otherwise fall back to the default communications
        // device (or the first device), so a vanished device degrades gracefully (never a dead id).
        if (!string.IsNullOrWhiteSpace(previous) && MicDevices.Any(d => d.Id == previous))
        {
            SelectedMicDeviceId = previous;
        }
        else
        {
            SelectedMicDeviceId = MicDevices.FirstOrDefault(d => d.IsDefaultCommunications)?.Id
                ?? MicDevices.FirstOrDefault()?.Id;
        }
    }
}
