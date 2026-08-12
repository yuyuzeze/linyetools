namespace KikuCaption.App.ViewModels;

/// <summary>
/// An immutable meeting capture-target choice (UI-R2 dialog draft). Internal codes are stable:
/// <c>"screen"</c> or <c>"window"</c>; <see cref="WindowTitle"/> is only meaningful for window capture.
/// </summary>
public sealed record MeetingCaptureTarget(string CaptureType, string? WindowTitle)
{
    public const string Screen = "screen";
    public const string Window = "window";

    public static MeetingCaptureTarget ScreenTarget { get; } = new(Screen, null);

    public bool IsWindow => string.Equals(CaptureType, Window, StringComparison.OrdinalIgnoreCase);

    /// <summary>A window target must name a window; a screen target is always valid.</summary>
    public bool IsValid => !IsWindow || !string.IsNullOrWhiteSpace(WindowTitle);
}

/// <summary>
/// Immutable meeting audio-input choice (UI-R5A): whether to capture system audio and/or the
/// microphone, and which microphone (stable WASAPI endpoint id; null = default communications device).
/// </summary>
public sealed record MeetingAudioOptions(bool RecordSystemAudio, bool RecordMicrophone, string? MicrophoneDeviceId)
{
    /// <summary>Defaults: system audio on, microphone on, default communications device.</summary>
    public static MeetingAudioOptions Default { get; } = new(true, true, null);

    /// <summary>At least one input must be selected to start a meeting.</summary>
    public bool HasAnyInput => RecordSystemAudio || RecordMicrophone;
}

/// <summary>
/// The one place the start dialog is allowed to write the meeting capture target and audio inputs.
/// The draft is only applied here — once, on confirm — so cancelling the dialog never mutates the live
/// meeting state (UI-R2 dialog-draft fix). Implemented by <see cref="RealtimeCaptionViewModel"/>.
/// </summary>
public interface IMeetingCaptureTargetSink
{
    /// <summary>The current live capture target (used to seed the dialog draft).</summary>
    MeetingCaptureTarget CaptureTarget { get; }

    /// <summary>The current live audio-input choice (used to seed the dialog draft).</summary>
    MeetingAudioOptions AudioOptions { get; }

    /// <summary>Applies a chosen target to the live meeting state.</summary>
    void ApplyCaptureTarget(MeetingCaptureTarget target);

    /// <summary>Applies the chosen audio inputs to the live meeting state.</summary>
    void ApplyAudioOptions(MeetingAudioOptions options);
}
