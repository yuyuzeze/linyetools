namespace KikuCaption.Core.Models;

/// <summary>Why a model is being resolved — the four entry points that load a Whisper model.</summary>
public enum WhisperModelPurpose
{
    /// <summary>Live realtime caption recognition (small).</summary>
    Realtime,

    /// <summary>WAV/file recognition (small).</summary>
    WavRecognition,

    /// <summary>Background prewarm of the realtime model (small).</summary>
    Prewarm,

    /// <summary>Post-meeting corrected-caption re-transcription (medium).</summary>
    PostMeetingCorrection
}

/// <summary>How thoroughly an installed model has been verified.</summary>
public enum ModelVerificationLevel
{
    /// <summary>Not verified (no receipt / unknown).</summary>
    None,

    /// <summary>Files present and model.bin is a plausible size — but not runtime-loaded.</summary>
    StructureVerified,

    /// <summary>Actually loaded by faster-whisper / CTranslate2 successfully.</summary>
    RuntimeVerified,

    /// <summary>A runtime load was attempted and failed.</summary>
    Failed
}

/// <summary>
/// The single result of resolving a model name to a concrete location (R7B.1). When
/// <see cref="IsManagedInstall"/> is true, <see cref="ModelName"/> is the ABSOLUTE managed directory
/// that must be passed to the Python worker (so faster-whisper loads that exact folder rather than
/// re-deriving "small" + a download root). When false, it is the legacy model name.
/// </summary>
public sealed record WhisperModelResolution(
    string ModelId,
    string ModelName,
    string ResolvedPath,
    bool IsManagedInstall,
    ModelVerificationLevel VerificationLevel,
    bool Exists);
