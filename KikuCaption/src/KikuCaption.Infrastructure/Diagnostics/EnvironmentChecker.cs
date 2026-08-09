using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Runs every registered <see cref="IEnvironmentProbe"/> and aggregates the results.
/// A single failing probe is isolated (recorded as an Error result) and never aborts the
/// whole check, so the app can always show the user a complete report.
/// </summary>
public sealed class EnvironmentChecker : IEnvironmentChecker
{
    private readonly IReadOnlyList<IEnvironmentProbe> _probes;
    private readonly ILogger<EnvironmentChecker> _logger;

    public EnvironmentChecker(IEnumerable<IEnvironmentProbe> probes, ILogger<EnvironmentChecker> logger)
    {
        _probes = probes.ToList();
        _logger = logger;
    }

    public async Task<EnvironmentReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DependencyCheckResult>(_probes.Count);

        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
                results.Add(result);
                _logger.LogInformation(
                    "Environment check {Kind}: {Status} ({Version})",
                    result.Kind, result.Status, result.DetectedVersion ?? "n/a");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Environment probe {Probe} failed", probe.DisplayName);
                results.Add(new DependencyCheckResult
                {
                    Kind = probe.Kind,
                    Name = probe.DisplayName,
                    IsRequired = false,
                    Status = EnvironmentCheckStatus.Error,
                    Detail = "检查该依赖时发生意外错误。",
                    Remediation = "请查看日志了解详情。"
                });
            }
        }

        return new EnvironmentReport(results);
    }
}
