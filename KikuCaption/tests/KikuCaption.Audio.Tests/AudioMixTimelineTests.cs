using KikuCaption.Audio.Mixing;
using Xunit;

namespace KikuCaption.Audio.Tests;

/// <summary>
/// UI-R5A: deterministic two-input mixer math on an injected clock — saturation, per-input silence
/// fill, bounded buffers, and zero accumulated drift over 30 minutes of logical time.
/// </summary>
public class AudioMixTimelineTests
{
    private const int Rate = AudioMixTimeline.SampleRate; // 16000

    // Builds `ms` of constant-valued 16 kHz mono int16 PCM.
    private static byte[] Tone(short value, int ms)
    {
        int samples = Rate * ms / 1000;
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    private static short SampleAt(byte[] pcm, int index) => (short)(pcm[index * 2] | (pcm[index * 2 + 1] << 8));

    [Fact] // scenario 6: only system audio → mixed equals the system signal
    public void SystemOnly_PassesThrough()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(1000, 100));
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(100));

        Assert.Equal(Rate / 10 * 2, outp.Length);
        Assert.Equal(1000, SampleAt(outp, 0));
        Assert.Equal(1000, SampleAt(outp, outp.Length / 2 - 1));
    }

    [Fact] // scenario 7: only microphone → mixed equals the mic signal
    public void MicOnly_PassesThrough()
    {
        var mix = new AudioMixTimeline();
        mix.AppendMic(Tone(-2000, 100));
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(100));
        Assert.Equal(-2000, SampleAt(outp, 0));
    }

    [Fact] // scenario 8: both inputs sum sample-by-sample
    public void BothInputs_AreSummed()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(1000, 100));
        mix.AppendMic(Tone(1500, 100));
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2500, SampleAt(outp, 0));
    }

    [Fact] // scenario 12: saturating add — never wraps around at the int16 rails
    public void Saturates_NeverWraps()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(30000, 20));
        mix.AppendMic(Tone(30000, 20));   // 60000 > 32767 → clamp, not wrap
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(20));
        Assert.Equal(short.MaxValue, SampleAt(outp, 0));

        var mix2 = new AudioMixTimeline();
        mix2.AppendSystem(Tone(-30000, 20));
        mix2.AppendMic(Tone(-30000, 20)); // -60000 < -32768 → clamp
        var outp2 = mix2.ProduceUpTo(TimeSpan.FromMilliseconds(20));
        Assert.Equal(short.MinValue, SampleAt(outp2, 0));
        Assert.True(mix2.GetMetrics(TimeSpan.FromMilliseconds(20)).ClippedSamples > 0);
    }

    [Fact] // scenario 9: one input silent does not shorten the mixed timeline
    public void OneInputSilent_DoesNotShortenTimeline()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(500, 100)); // mic contributes nothing
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(100));
        Assert.Equal(Rate / 10, outp.Length / 2); // full 100 ms produced
        var m = mix.GetMetrics(TimeSpan.FromMilliseconds(100));
        Assert.Equal(Rate / 10, m.MicSilenceSamples); // mic filled with silence
        Assert.Equal(Rate / 10, m.SystemRealSamples);
    }

    [Fact] // scenario 10: a late-starting microphone loses no system audio
    public void LateMicrophone_LosesNoSystemAudio()
    {
        var mix = new AudioMixTimeline();
        // First 200 ms: system only.
        mix.AppendSystem(Tone(1000, 200));
        var first = mix.ProduceUpTo(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1000, SampleAt(first, 0)); // system preserved (mic silent)

        // Mic joins for the next 200 ms alongside system.
        mix.AppendSystem(Tone(1000, 200));
        mix.AppendMic(Tone(2000, 200));
        var second = mix.ProduceUpTo(TimeSpan.FromMilliseconds(400));
        Assert.Equal(3000, SampleAt(second, 0));

        var m = mix.GetMetrics(TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, m.SystemDroppedSamples);       // no system loss
        Assert.Equal(Rate * 400 / 1000, m.SystemRealSamples);
    }

    [Fact] // scenario 11: a paused loopback does not drop the microphone
    public void PausedLoopback_KeepsMicrophone()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(1000, 100)); // system then goes quiet
        mix.AppendMic(Tone(1500, 300));    // mic keeps going
        var outp = mix.ProduceUpTo(TimeSpan.FromMilliseconds(300));

        Assert.Equal(Rate * 3 / 10, outp.Length / 2);       // full 300 ms
        Assert.Equal(2500, SampleAt(outp, 0));              // system + mic while both present
        Assert.Equal(1500, SampleAt(outp, Rate * 2 / 10));  // later: mic only (system silent)
    }

    [Fact] // scenario 13: buffers are bounded — an over-fast input drops oldest and is counted
    public void Buffers_AreBounded()
    {
        var mix = new AudioMixTimeline(frameMilliseconds: 20, maxJitterSeconds: 0.1); // 100 ms cap
        mix.AppendSystem(Tone(1000, 500)); // 500 ms into a 100 ms buffer
        var m = mix.GetMetrics(TimeSpan.Zero);
        Assert.True(m.SystemDroppedSamples > 0);
        Assert.True(m.MaxSystemDepthBytes <= Rate * 2 * 0.1 + 4);
    }

    [Fact] // scenario 20: 30 minutes of logical time — no accumulated drift
    public void ThirtyMinutes_NoDrift()
    {
        var mix = new AudioMixTimeline();
        var total = TimeSpan.FromMinutes(30);
        // Feed exactly 30 minutes of continuous system audio in 1 s steps, producing as we go.
        for (int sec = 1; sec <= 1800; sec++)
        {
            mix.AppendSystem(Tone(1000, 1000));
            mix.ProduceUpTo(TimeSpan.FromSeconds(sec));
        }

        var m = mix.GetMetrics(total);
        long expected = AudioMixTimeline.ExpectedSamples(total);
        Assert.Equal(expected, m.MixedSamples);          // frame-aligned to the exact second → no drift
        Assert.Equal(0.0, m.ClockErrorMs, 3);
        Assert.Equal(0, m.SystemDroppedSamples);
    }

    [Fact] // sub-frame requests produce nothing (whole-frame output only)
    public void SubFrame_ProducesNothing()
    {
        var mix = new AudioMixTimeline(frameMilliseconds: 20);
        mix.AppendSystem(Tone(1000, 20));
        Assert.Empty(mix.ProduceUpTo(TimeSpan.FromMilliseconds(10))); // < one 20 ms frame
    }

    [Fact] // Flush pads to the exact session end (no over-write, mixes remaining PCM)
    public void Flush_PadsToSessionEnd()
    {
        var mix = new AudioMixTimeline();
        mix.AppendSystem(Tone(1000, 55));
        mix.ProduceUpTo(TimeSpan.FromMilliseconds(40)); // 2 frames
        var tail = mix.Flush(TimeSpan.FromMilliseconds(100));

        long expected = AudioMixTimeline.ExpectedSamples(TimeSpan.FromMilliseconds(100));
        Assert.Equal(expected, mix.GetMetrics(TimeSpan.FromMilliseconds(100)).MixedSamples);
        Assert.True(tail.Length > 0);
    }
}
