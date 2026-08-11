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

    /// <summary>
    /// Aggregated health for the top-bar indicator (UI-R1 §4):
    /// <list type="bullet">
    ///   <item><see cref="EnvironmentHealth.Blocked"/> (red) — a required dependency is missing or
    ///     errored, so captions cannot start;</item>
    ///   <item><see cref="EnvironmentHealth.Degraded"/> (yellow) — nothing required is broken, but a
    ///     non-critical capability is missing/warning (e.g. FFmpeg or translation), so captions run
    ///     while recording/translation may not;</item>
    ///   <item><see cref="EnvironmentHealth.Healthy"/> (green) — everything is OK.</item>
    /// </list>
    /// <see cref="EnvironmentHealth.Unknown"/> (grey) is a UI-only state (not-yet-checked /
    /// checking) and is therefore never produced here.
    /// </summary>
    public EnvironmentHealth OverallHealth
    {
        get
        {
            if (Results.Count == 0)
            {
                return EnvironmentHealth.Healthy;
            }

            if (HasBlockingIssues)
            {
                return EnvironmentHealth.Blocked;
            }

            var anyDegraded = Results.Any(r =>
                r.Status is EnvironmentCheckStatus.Warning
                         or EnvironmentCheckStatus.Missing
                         or EnvironmentCheckStatus.Error);

            return anyDegraded ? EnvironmentHealth.Degraded : EnvironmentHealth.Healthy;
        }
    }
}
