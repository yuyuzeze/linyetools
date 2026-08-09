using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Checks a single external dependency. Implementations must not throw for the
/// "dependency is missing" case &mdash; they return a <see cref="DependencyCheckResult"/>
/// with the appropriate status instead.
/// </summary>
public interface IEnvironmentProbe
{
    DependencyKind Kind { get; }

    /// <summary>Display name used if the probe itself fails unexpectedly.</summary>
    string DisplayName { get; }

    Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken);
}
