using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Models;

/// <summary>
/// Outcome of checking a single external dependency (.NET, Python, FFmpeg, disk space).
/// Messages are user-facing and must never contain secrets.
/// </summary>
public sealed record DependencyCheckResult
{
    public required DependencyKind Kind { get; init; }

    /// <summary>Human-readable dependency name shown in the UI.</summary>
    public required string Name { get; init; }

    public required EnvironmentCheckStatus Status { get; init; }

    /// <summary>Detected version string, when the dependency was found.</summary>
    public string? DetectedVersion { get; init; }

    /// <summary>Short explanation of the current state.</summary>
    public string? Detail { get; init; }

    /// <summary>Actionable hint for the user when something is missing or sub-optimal.</summary>
    public string? Remediation { get; init; }

    /// <summary>
    /// True when the app cannot deliver its full feature set without this dependency.
    /// Required dependencies that are missing produce a blocking (but non-crashing) warning.
    /// </summary>
    public bool IsRequired { get; init; }
}
