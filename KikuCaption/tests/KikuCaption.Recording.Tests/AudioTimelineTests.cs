using KikuCaption.Recording.Muxing;
using Xunit;

namespace KikuCaption.Recording.Tests;

public class AudioTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static byte[] Frame(byte value, int samples = 320)
    {
        var b = new byte[samples * 2];
        Array.Fill(b, value);
        return b;
    }

    private static bool AllZero(byte[] b) => b.All(x => x == 0);

    [Fact] // 1: startup with no PCM produces digital silence
    public void StartupNoPcm_ProducesSilence()
    {
        var t = new AudioTimeline();
        var pcm = t.ProduceUpTo(S(0.1)); // 1600 samples = 5 frames
        Assert.Equal(1600 * 2, pcm.Length);
        Assert.True(AllZero(pcm));
        Assert.Equal(1600, t.WrittenSamples);
        Assert.Equal(1600, t.GetMetrics(S(0.1)).InsertedSilenceSamples);
    }

    [Fact] // 6: 20 ms frame is 320 samples
    public void FrameIs320Samples()
    {
        Assert.Equal(320, new AudioTimeline(frameMilliseconds: 20).FrameSamples);
    }

    [Fact] // 8, 10: real PCM is placed and consumed in FIFO order
    public void RealPcm_PlacedInOrder()
    {
        var t = new AudioTimeline();
        t.AppendRealPcm(Frame(0x11));
        t.AppendRealPcm(Frame(0x22));

        var first = t.ProduceUpTo(S(0.02));  // one frame
        var second = t.ProduceUpTo(S(0.04));  // next frame
        Assert.All(first, b => Assert.Equal(0x11, b));
        Assert.All(second, b => Assert.Equal(0x22, b));
        Assert.Equal(640, t.GetMetrics(S(0.04)).RealSamplesWritten);
    }

    [Fact] // 2, 3: real then silence padding
    public void RealThenSilence_Padded()
    {
        var t = new AudioTimeline();
        t.AppendRealPcm(Frame(0x33)); // one real frame
        var real = t.ProduceUpTo(S(0.02));
        var silence = t.ProduceUpTo(S(0.06)); // two more frames, no real → silence
        Assert.All(real, b => Assert.Equal(0x33, b));
        Assert.True(AllZero(silence));
        var m = t.GetMetrics(S(0.06));
        Assert.Equal(320, m.RealSamplesWritten);
        Assert.Equal(640, m.InsertedSilenceSamples);
    }

    [Fact] // 4: full silence — duration still tracks the clock
    public void FullSilence_DurationTracksClock()
    {
        var t = new AudioTimeline();
        var pcm = t.ProduceUpTo(S(1.0));
        Assert.Equal(16000, t.WrittenSamples);
        Assert.Equal(0, t.GetMetrics(S(1.0)).RealSamplesWritten);
        Assert.True(AllZero(pcm));
    }

    [Fact] // 5: target sample count
    public void ExpectedSamples_Computed()
    {
        Assert.Equal(1_920_000, AudioTimeline.ExpectedSamples(TimeSpan.FromMinutes(2)));
        Assert.Equal(28_800_000, AudioTimeline.ExpectedSamples(TimeSpan.FromMinutes(30)));
    }

    [Fact] // 9, 11: jitter buffer is bounded; overflow drops and counts
    public void JitterBuffer_Bounded_DropsOverflow()
    {
        var t = new AudioTimeline(maxJitterSeconds: 1.0); // 32000 bytes cap
        for (int i = 0; i < 150; i++) // 150 * 320 samples = 48000 samples of real audio (1.5 s)
        {
            t.AppendRealPcm(Frame(0x44));
        }

        var m = t.GetMetrics(S(0));
        Assert.True(m.DroppedLateSamples > 0, "expected overflow drops");
        Assert.True(m.MaxBufferDepthBytes <= 32000, $"buffer exceeded cap: {m.MaxBufferDepthBytes}");
    }

    [Fact] // 7, 16: no drift over 2 minutes with jittery scheduling
    public void NoDrift_TwoMinutes()
    {
        var t = new AudioTimeline();
        double now = 0;
        var rnd = new Random(1);
        while (now < 120.0)
        {
            now = Math.Min(120.0, now + 0.015 + rnd.NextDouble() * 0.01); // 15–25 ms jitter
            t.ProduceUpTo(S(now));
        }

        Assert.Equal(1_920_000, t.WrittenSamples); // exactly floor(120 s × 16000), frame-aligned
    }

    [Fact] // 17: no drift over 30 minutes (simulated clock, no real waiting)
    public void NoDrift_ThirtyMinutes()
    {
        var t = new AudioTimeline();
        for (int sec = 1; sec <= 1800; sec++)
        {
            t.ProduceUpTo(S(sec));
        }

        Assert.Equal(28_800_000, t.WrittenSamples);
    }

    [Fact] // 14: flush pads to the target sample count
    public void Flush_PadsToTarget()
    {
        var t = new AudioTimeline();
        t.ProduceUpTo(S(0.1)); // 1600 samples
        var tail = t.Flush(S(0.15)); // pad to 2400 → 800 more samples
        Assert.Equal(2400, t.WrittenSamples);
        Assert.Equal(800 * 2, tail.Length);
    }

    [Fact] // 15: flush never over-writes past what was produced
    public void Flush_NoOverwrite()
    {
        var t = new AudioTimeline();
        t.ProduceUpTo(S(0.2)); // 3200 samples
        var tail = t.Flush(S(0.15)); // target 2400 < 3200 → nothing
        Assert.Empty(tail);
        Assert.Equal(3200, t.WrittenSamples);
    }
}
