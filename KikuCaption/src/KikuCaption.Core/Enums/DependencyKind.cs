namespace KikuCaption.Core.Enums;

/// <summary>
/// Identifies which external dependency an environment check refers to.
/// </summary>
public enum DependencyKind
{
    DotNetRuntime,
    Python,
    FFmpeg,
    DiskSpace
}
