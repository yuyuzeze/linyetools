namespace KikuCaption.Core.Enums;

/// <summary>
/// Identifies which external dependency an environment check refers to.
/// </summary>
public enum DependencyKind
{
    DotNetRuntime,
    Python,
    FFmpeg,
    DiskSpace,

    // Added in UI-R1 so the environment page can show the full dependency set (PROJECT.md §5).
    WhisperWorker,
    WhisperModel,
    FFprobe,
    AudioOutputDevice,
    OutputDirectory,
    TranslationApi
}
