using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

public class EnvironmentReportTests
{
    private static DependencyCheckResult Result(EnvironmentCheckStatus status, bool required) => new()
    {
        Kind = DependencyKind.Python,
        Name = "test",
        Status = status,
        IsRequired = required
    };

    [Fact]
    public void OverallStatus_IsMostSevere()
    {
        var report = new EnvironmentReport(new[]
        {
            Result(EnvironmentCheckStatus.Ok, true),
            Result(EnvironmentCheckStatus.Warning, false),
            Result(EnvironmentCheckStatus.Missing, true)
        });

        Assert.Equal(EnvironmentCheckStatus.Missing, report.OverallStatus);
    }

    [Fact]
    public void HasBlockingIssues_TrueOnlyForRequiredMissingOrError()
    {
        var blocking = new EnvironmentReport(new[] { Result(EnvironmentCheckStatus.Missing, required: true) });
        var nonBlocking = new EnvironmentReport(new[] { Result(EnvironmentCheckStatus.Missing, required: false) });
        var warningOnly = new EnvironmentReport(new[] { Result(EnvironmentCheckStatus.Warning, required: true) });

        Assert.True(blocking.HasBlockingIssues);
        Assert.False(nonBlocking.HasBlockingIssues);
        Assert.False(warningOnly.HasBlockingIssues);
    }

    [Fact]
    public void EmptyReport_IsOk()
    {
        var report = new EnvironmentReport(Array.Empty<DependencyCheckResult>());

        Assert.Equal(EnvironmentCheckStatus.Ok, report.OverallStatus);
        Assert.False(report.HasBlockingIssues);
    }
}
