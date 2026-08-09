namespace KikuCaption.Core.Enums;

/// <summary>
/// Result severity of a single environment dependency check.
/// Ordered from least to most severe so the overall status can be
/// computed as the maximum value.
/// </summary>
public enum EnvironmentCheckStatus
{
    /// <summary>Dependency is present and usable.</summary>
    Ok = 0,

    /// <summary>Dependency is present but sub-optimal (e.g. low disk space, unexpected version).</summary>
    Warning = 1,

    /// <summary>A required dependency could not be found.</summary>
    Missing = 2,

    /// <summary>The check itself failed unexpectedly.</summary>
    Error = 3
}
