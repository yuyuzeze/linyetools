namespace KikuCaption.Core.Enums;

/// <summary>
/// Aggregated, user-facing environment health for the top-bar status indicator (UI-R1 §4).
/// Never expressed by colour alone — the UI always pairs it with text and a tooltip.
/// </summary>
public enum EnvironmentHealth
{
    /// <summary>Not checked yet, or a check is currently running (grey).</summary>
    Unknown = 0,

    /// <summary>Every required dependency is present and usable (green).</summary>
    Healthy = 1,

    /// <summary>
    /// A non-critical capability (recording / translation) is unavailable, but captions still run
    /// (yellow).
    /// </summary>
    Degraded = 2,

    /// <summary>A critical dependency is missing, so the core feature cannot start (red).</summary>
    Blocked = 3
}
