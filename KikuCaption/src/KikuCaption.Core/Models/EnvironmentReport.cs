using System.Linq;
using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Models;

/// <summary>
/// Aggregated result of all environment dependency checks.
/// </summary>
public sealed record EnvironmentReport
{
    public EnvironmentReport(IReadOnlyList<DependencyCheckResult> results)
    {
        Results = results;
    }

    public IReadOnlyList<DependencyCheckResult> Results { get; }

    /// <summary>The most severe status across all checks.</summary>
    public EnvironmentCheckStatus OverallStatus =>
        Results.Count == 0
            ? EnvironmentCheckStatus.Ok
            : (EnvironmentCheckStatus)Results.Max(r => (int)r.Status);

    /// <summary>
    /// True when a <em>required</em> dependency is missing or errored. Blocking issues are
    /// surfaced to the user but must never crash the application (PROJECT.md 17, M0).
    /// </summary>
    public bool HasBlockingIssues =>
        Results.Any(r => r.IsRequired &&
            r.Status is EnvironmentCheckStatus.Missing or EnvironmentCheckStatus.Error);
}
