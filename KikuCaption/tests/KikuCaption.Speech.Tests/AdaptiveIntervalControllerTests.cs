using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class AdaptiveIntervalControllerTests
{
    private const int Min = 600;
    private const int Max = 2000;

    [Fact] // no decode sample yet → hold the current value (clamped)
    public void NoSample_HoldsCurrent()
    {
        Assert.Equal(2000, AdaptiveIntervalController.Next(0, 2000, Min, Max));
        Assert.Equal(Min, AdaptiveIntervalController.Next(-1, 100, Min, Max)); // clamp a stray low current
    }

    [Fact] // fast decode pulls the interval DOWN toward the floor (lower latency)
    public void FastDecode_TightensTowardFloor()
    {
        // decode 300 ms, current 2000 → target 600, EMA 0.5*600+0.5*2000 = 1300
        Assert.Equal(1300, AdaptiveIntervalController.Next(300, 2000, Min, Max));

        // iterate with a sustained fast decode → converges to the floor, never below it
        int cur = 2000;
        for (int i = 0; i < 30; i++) cur = AdaptiveIntervalController.Next(250, cur, Min, Max);
        Assert.Equal(Min, cur); // target 500 < floor → clamped at 600
    }

    [Fact] // slow decode pushes the interval UP, never above the ceiling (no falling behind)
    public void SlowDecode_BacksOffTowardCeiling()
    {
        // decode 1500 ms, current 600 → target 3000, EMA 1800, within range
        Assert.Equal(1800, AdaptiveIntervalController.Next(1500, 600, Min, Max));

        // a very slow decode is capped at the ceiling (== the fixed value)
        int cur = 600;
        for (int i = 0; i < 30; i++) cur = AdaptiveIntervalController.Next(5000, cur, Min, Max);
        Assert.Equal(Max, cur);
    }

    [Fact] // a sustained medium decode converges to ~2x decode, inside the band
    public void MediumDecode_ConvergesToTwiceDecode()
    {
        int cur = Max;
        for (int i = 0; i < 40; i++) cur = AdaptiveIntervalController.Next(500, cur, Min, Max);
        Assert.InRange(cur, 980, 1020); // 2 * 500 ms = ~1000 ms
    }

    [Fact] // result is always within [min, max]
    public void AlwaysWithinBand()
    {
        foreach (var decode in new long[] { 1, 50, 300, 700, 1200, 4000, 100000 })
        foreach (var cur in new[] { Min, 1000, Max })
        {
            int r = AdaptiveIntervalController.Next(decode, cur, Min, Max);
            Assert.InRange(r, Min, Max);
        }
    }
}
