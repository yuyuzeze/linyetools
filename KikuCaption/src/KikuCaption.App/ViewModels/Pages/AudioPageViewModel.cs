using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Audio page view model (UI-R2 §7). Hosts the two diagnostic/test tools moved off the home page:
/// system audio capture (WAV) and local WAV speech recognition. It only aggregates the existing
/// sub-view models — no capture/recognition logic lives here, and behaviour is unchanged.
/// </summary>
public sealed partial class AudioPageViewModel : ObservableObject
{
    public AudioPageViewModel(AudioCaptureViewModel capture, SpeechViewModel speech)
    {
        Capture = capture;
        Speech = speech;
    }

    /// <summary>System-audio capture (WAV) tool.</summary>
    public AudioCaptureViewModel Capture { get; }

    /// <summary>Local speech recognition (WAV) tool.</summary>
    public SpeechViewModel Speech { get; }
}
