using System.Diagnostics;
using KikuCaption.Infrastructure.Diagnostics;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

public class CpuUsageCalculatorTests
{
    [Fact] // 26: reproducible calculation
    public void Percent_NormalizesByCores()
    {
        // 1000ms CPU over 1000ms wall on 4 cores = 25% of the machine.
        Assert.Equal(25.0, CpuUsageCalculator.Percent(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000), 4), 3);
        // Fully busy on all 4 cores = 100%.
        Assert.Equal(100.0, CpuUsageCalculator.Percent(TimeSpan.FromMilliseconds(4000), TimeSpan.FromMilliseconds(1000), 4), 3);
    }

    [Fact]
    public void Percent_ClampsAndGuards()
    {
        Assert.Equal(100.0, CpuUsageCalculator.Percent(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(1), 1), 3);
        Assert.Equal(0.0, CpuUsageCalculator.Percent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Equal(0.0, CpuUsageCalculator.Percent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
        Assert.Equal(0.0, CpuUsageCalculator.Percent(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), 4));
    }

    [Fact] // 27: 60-min simulated clock never overflows or leaves [0,100]
    public void SixtyMinuteSimulation_StaysBounded()
    {
        var rng = new Random(7);
        var elapsed = TimeSpan.FromSeconds(1);
        for (int sec = 0; sec < 3600; sec++)
        {
            // Random CPU delta up to all-cores-busy for a 1s window.
            var cpuDelta = TimeSpan.FromMilliseconds(rng.NextDouble() * 1000 * Environment.ProcessorCount);
            var pct = CpuUsageCalculator.Percent(cpuDelta, elapsed, Environment.ProcessorCount);
            Assert.InRange(pct, 0.0, 100.0);
        }
    }
}

public class ProcessCpuSamplerTests
{
    [Fact]
    public void FirstSample_IsNull_ThenValueInRange()
    {
        var sampler = new ProcessCpuSampler(Environment.ProcessorCount);
        using var self = Process.GetCurrentProcess();

        Assert.Null(sampler.Sample(self)); // needs two points

        // Do a little work so some CPU time elapses.
        double x = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30) { x += Math.Sqrt(sw.ElapsedTicks + 1); }
        Assert.True(x >= 0);

        var pct = sampler.Sample(self);
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 0.0, 100.0);
    }

    [Fact] // dead process → null, never 0%
    public void ExitedProcess_ReturnsNull()
    {
        var sampler = new ProcessCpuSampler();
        var psi = new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Null(sampler.Sample(p));
        Assert.Null(ProcessCpuSampler.WorkingSetBytes(p));
    }
}

public class DiagnosticsFormatterTests
{
    [Fact]
    public void LogLine_ContainsMetrics_NoSensitiveText()
    {
        var snap = new DiagnosticsSnapshot
        {
            MainCpuPercent = 12, WorkerCpuPercent = 30, FfmpegCpuPercent = 8,
            MainWorkingSet = 200L * 1024 * 1024, Rtf = 0.21, LastInferenceMs = 180,
            AudioQueueDepthMs = 400, DroppedAudioChunks = 0, TranslationQueueDepth = 2,
            FreeDiskGb = 17.6, Mp4Bytes = 5L * 1024 * 1024, Mp4GrowthKbPerSec = 22.5
        };

        var line = DiagnosticsFormatter.ToLogLine(snap);
        Assert.Contains("perf cpu", line);
        Assert.Contains("total=50%", line); // 12+30+8
        Assert.Contains("rtf=0.21", line);
        Assert.Contains("disk=17.6GB", line);
        // No caption/translation/PCM/title/key can appear — the snapshot has no such fields.
        Assert.DoesNotContain("Bearer", line);
    }

    [Fact]
    public void Health_ReportsDiskAndCpu()
    {
        Assert.Equal("磁盘不足", DiagnosticsFormatter.HealthLabel(new DiagnosticsSnapshot { FreeDiskGb = 0.5 }, 2));
        Assert.Equal("CPU 偏高", DiagnosticsFormatter.HealthLabel(new DiagnosticsSnapshot { FreeDiskGb = 20, MainCpuPercent = 95 }, 2));
        Assert.Equal("运行正常", DiagnosticsFormatter.HealthLabel(new DiagnosticsSnapshot { FreeDiskGb = 20, MainCpuPercent = 20 }, 2));
    }
}
