using System.Diagnostics;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Stateful per-process CPU sampler (Milestone 7 §5). Keeps the last <c>TotalProcessorTime</c> and a
/// monotonic timestamp; each <see cref="Sample"/> returns the CPU% since the previous call. Returns
/// <c>null</c> (not 0%) when there is no prior sample yet, or when the process has exited/is
/// inaccessible — so a dead process is never reported as 0% busy.
/// </summary>
public sealed class ProcessCpuSampler
{
    private readonly int _logicalCores;
    private TimeSpan? _lastCpu;
    private long _lastTimestamp;

    public ProcessCpuSampler(int? logicalCores = null)
    {
        _logicalCores = logicalCores is > 0 ? logicalCores.Value : Environment.ProcessorCount;
    }

    /// <summary>Current working set (bytes) or null if the process has exited/is inaccessible.</summary>
    public static long? WorkingSetBytes(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            if (process.HasExited)
            {
                return null;
            }

            process.Refresh();
            return process.WorkingSet64;
        }
        catch
        {
            return null;
        }
    }

    public double? Sample(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        TimeSpan cpu;
        try
        {
            if (process.HasExited)
            {
                _lastCpu = null; // reset so a restarted PID doesn't produce a bogus delta
                return null;
            }

            cpu = process.TotalProcessorTime;
        }
        catch
        {
            _lastCpu = null;
            return null;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastCpu is null)
        {
            _lastCpu = cpu;
            _lastTimestamp = now;
            return null; // need two points to compute a rate
        }

        var elapsed = TimeSpan.FromSeconds((now - _lastTimestamp) / (double)Stopwatch.Frequency);
        var delta = cpu - _lastCpu.Value;
        _lastCpu = cpu;
        _lastTimestamp = now;

        return CpuUsageCalculator.Percent(delta, elapsed, _logicalCores);
    }
}
