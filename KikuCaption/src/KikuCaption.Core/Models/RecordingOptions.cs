using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;

namespace KikuCaption.Core.Models;

/// <summary>
/// Options for one recording session (PROJECT.md 8.2). Captures the target, output, frame rate,
/// the (already resolved) encoder, and the system-audio format fed to FFmpeg.
/// </summary>
public sealed record RecordingOptions
{
    public required CaptureTargetType CaptureType { get; init; }

    /// <summary>Window title to capture (required when <see cref="CaptureType"/> is Window).</summary>
    public string? TargetTitle { get; init; }

    /// <summary>Optional window handle used to re-verify the target still exists.</summary>
    public nint TargetWindowHandle { get; init; }

    public required string OutputPath { get; init; }

    public required string FFmpegPath { get; init; }

    public int FrameRate { get; init; } = 15;

    /// <summary>The effective H.264 encoder to use (resolved by capability probe): h264_qsv or libx264.</summary>
    public string Encoder { get; init; } = "libx264";

    public bool IncludeSystemAudio { get; init; } = true;

    public int AudioSampleRate { get; init; } = 16000;

    public int AudioChannels { get; init; } = 1;

    /// <summary>
    /// UI-R5A: when set, the recorder consumes this already-mixed audio source (system + microphone
    /// from the session mixer) instead of opening its own WASAPI loopback — so exactly one loopback
    /// exists and the recording hears the same mix as the live captions. Null keeps the legacy
    /// behavior (the recorder opens its own loopback). Not serialized; wired at runtime only.
    /// </summary>
    public IAudioCaptureService? ExternalAudioSource { get; init; }
}
