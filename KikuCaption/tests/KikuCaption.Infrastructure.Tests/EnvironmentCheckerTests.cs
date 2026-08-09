using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

public class EnvironmentCheckerTests
{
    private sealed class FakeProbe : IEnvironmentProbe
    {
        private readonly DependencyCheckResult _result;
        public FakeProbe(DependencyKind kind, EnvironmentCheckStatus status, bool required)
        {
            Kind = kind;
            _result = new DependencyCheckResult
            {
                Kind = kind, Name = kind.ToString(), Status = status, IsRequired = required
            };
        }

        public DependencyKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingProbe : IEnvironmentProbe
    {
        public DependencyKind Kind => DependencyKind.FFmpeg;
        public string DisplayName => "throwing";
        public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task CheckAsync_AggregatesAllProbeResults()
    {
        var checker = new EnvironmentChecker(
            new IEnvironmentProbe[]
            {
                new FakeProbe(DependencyKind.DotNetRuntime, EnvironmentCheckStatus.Ok, required: true),
                new FakeProbe(DependencyKind.Python, EnvironmentCheckStatus.Missing, required: true)
            },
            NullLogger<EnvironmentChecker>.Instance);

        var report = await checker.CheckAsync();

        Assert.Equal(2, report.Results.Count);
        Assert.True(report.HasBlockingIssues);
        Assert.Equal(EnvironmentCheckStatus.Missing, report.OverallStatus);
    }

    [Fact]
    public async Task CheckAsync_IsolatesProbeFailures()
    {
        var checker = new EnvironmentChecker(
            new IEnvironmentProbe[]
            {
                new FakeProbe(DependencyKind.DotNetRuntime, EnvironmentCheckStatus.Ok, required: true),
                new ThrowingProbe()
            },
            NullLogger<EnvironmentChecker>.Instance);

        var report = await checker.CheckAsync();

        Assert.Equal(2, report.Results.Count);
        Assert.Contains(report.Results, r => r.Status == EnvironmentCheckStatus.Error);
    }
}
