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
    /// Stable, language-neutral message code the App layer localizes into the user's UI language
    /// (UI-R3.1). Probes set this instead of emitting a localized sentence, so Core/Infrastructure
    /// never depend on the UI localization service. Null means fall back to <see cref="Detail"/>.
    /// </summary>
    public string? MessageCode { get; init; }

    /// <summary>
    /// Format arguments for <see cref="MessageCode"/> — raw values (drive name, GB, missing field
    /// names) that are inserted verbatim and never translated.
    /// </summary>
    public IReadOnlyList<string>? MessageArguments { get; init; }

    /// <summary>Stable message code for the remediation hint (localized in the App layer).</summary>
    public string? RemediationCode { get; init; }

    /// <summary>Format arguments for <see cref="RemediationCode"/> (raw values, never translated).</summary>
    public IReadOnlyList<string>? RemediationArguments { get; init; }

    /// <summary>
    /// Fully-resolved absolute path the check settled on (e.g. the located ffmpeg.exe, the whisper
    /// worker script, the output directory). Shown on the environment page so the user can see
    /// exactly what was used. Never contains secrets. Null when a path is not meaningful.
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>
    /// True when the app cannot deliver its full feature set without this dependency.
    /// Required dependencies that are missing produce a blocking (but non-crashing) warning.
    /// </summary>
    public bool IsRequired { get; init; }
}
