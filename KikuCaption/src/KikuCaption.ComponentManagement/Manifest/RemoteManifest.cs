namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>Component kinds the app will accept from a manifest. Anything else is rejected (R7A).</summary>
public enum RemoteComponentType
{
    /// <summary>A faster-whisper model directory (small / medium).</summary>
    WhisperModel,

    /// <summary>An FFmpeg + ffprobe bundle.</summary>
    FFmpegBundle,

    /// <summary>A native LibVLC runtime.</summary>
    LibVlcRuntime
}

/// <summary>The parsed, validated remote manifest (R7A). Never carries commands to execute.</summary>
public sealed record RemoteManifest(
    int SchemaVersion,
    string Product,
    string Channel,
    ApplicationRelease? Application,
    IReadOnlyList<RemoteComponent> Components);

/// <summary>An application release entry (used by R7D; parsed and validated here).</summary>
public sealed record ApplicationRelease(
    string Version,
    string PackageUrl,
    string Sha256,
    long SizeBytes,
    string? ReleaseNotesUrl,
    string? MinimumSupportedVersion);

/// <summary>A downloadable, verifiable component. <see cref="InstallDirectory"/> is always relative.</summary>
public sealed record RemoteComponent(
    string Id,
    RemoteComponentType Type,
    string Version,
    string Url,
    string Sha256,
    long SizeBytes,
    string InstallDirectory,
    IReadOnlyList<string> RequiredFiles);
