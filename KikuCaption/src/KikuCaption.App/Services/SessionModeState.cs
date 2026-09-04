using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.Services;

/// <summary>
/// The one place that holds the live "effective meeting mode" for the UI (UI-R6A). It mirrors the
/// persisted <c>EnableSpeechRecognition</c> setting so the Home page can show the current mode and
/// disable caption-only controls, without every view model re-reading <c>UserSettings</c>. The actual
/// per-meeting decision is still snapshotted into <c>SessionCapabilities</c> at start; this is only the
/// pre-meeting display/enable state. Seeded once at startup and updated when the setting is saved.
/// </summary>
public sealed partial class SessionModeState : ObservableObject
{
    /// <summary>True when the next meeting will run local speech recognition (captions/translation).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingOnly))]
    private bool _speechRecognitionEnabled = true;

    /// <summary>True when the next meeting will only record screen + audio (no captions).</summary>
    public bool IsRecordingOnly => !SpeechRecognitionEnabled;
}
