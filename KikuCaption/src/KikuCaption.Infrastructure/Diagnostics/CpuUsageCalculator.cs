namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Reproducible CPU% from a process's <c>TotalProcessorTime</c> delta over a wall-clock window,
/// normalized by logical processor count (Milestone 7 §5). Pure and testable. This replaces the
/// earlier unreliable sampling; a caller must never read CPU after a process has exited and report
/// it as 0% — see <see cref="ProcessCpuSampler"/>.
/// </summary>
public static class CpuUsageCalculator
{
    /// <summary>
    /// CPU percent for one process over <paramref name="elapsed"/>: 100 × cpuDelta ÷ (elapsed ×
    /// logicalCores), clamped to [0, 100]. Returns 0 for non-positive elapsed/cores.
    /// </summary>
    public static double Percent(TimeSpan cpuDelta, TimeSpan elapsed, int logicalCores)
    {
        if (elapsed <= TimeSpan.Zero || logicalCores <= 0 || cpuDelta < TimeSpan.Zero)
        {
            return 0;
        }

        double pct = 100.0 * cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * logicalCores);
        return Math.Clamp(pct, 0, 100);
    }
}
