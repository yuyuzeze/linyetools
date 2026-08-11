using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

/// <summary>UI-R1 §4 green/yellow/red aggregation for the top-bar indicator.</summary>
public class EnvironmentHealthTests
{
    private static DependencyCheckResult R(EnvironmentCheckStatus status, bool required) => new()
    {
        Kind = DependencyKind.Python,
        Name = "test",
        Status = status,
        IsRequired = required
    };

    [Fact] // green: everything OK
    public void AllOk_IsHealthy()
    {
        var report = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Ok, required: true),
            R(EnvironmentCheckStatus.Ok, required: false)
        });

        Assert.Equal(EnvironmentHealth.Healthy, report.OverallHealth);
    }

    [Fact] // yellow: a non-critical capability missing/warning, nothing required broken
    public void OptionalMissingOrWarning_IsDegraded()
    {
        var optionalMissing = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Ok, required: true),
            R(EnvironmentCheckStatus.Missing, required: false)
        });
        var requiredWarning = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Warning, required: true)
        });

        Assert.Equal(EnvironmentHealth.Degraded, optionalMissing.OverallHealth);
        Assert.Equal(EnvironmentHealth.Degraded, requiredWarning.OverallHealth);
    }

    [Fact] // red: a required dependency missing or errored
    public void RequiredMissingOrError_IsBlocked()
    {
        var missing = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Ok, required: false),
            R(EnvironmentCheckStatus.Missing, required: true)
        });
        var error = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Error, required: true)
        });

        Assert.Equal(EnvironmentHealth.Blocked, missing.OverallHealth);
        Assert.Equal(EnvironmentHealth.Blocked, error.OverallHealth);
    }

    [Fact] // red wins over yellow when both are present
    public void BlockedTakesPrecedenceOverDegraded()
    {
        var report = new EnvironmentReport(new[]
        {
            R(EnvironmentCheckStatus.Warning, required: false), // would be yellow alone
            R(EnvironmentCheckStatus.Missing, required: true)   // but this blocks
        });

        Assert.Equal(EnvironmentHealth.Blocked, report.OverallHealth);
    }

    [Fact]
    public void EmptyReport_IsHealthy()
    {
        Assert.Equal(EnvironmentHealth.Healthy, new EnvironmentReport(Array.Empty<DependencyCheckResult>()).OverallHealth);
    }
}
