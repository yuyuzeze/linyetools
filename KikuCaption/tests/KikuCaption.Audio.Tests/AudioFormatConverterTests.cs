using KikuCaption.Audio.Conversion;
using NAudio.Wave;
using Xunit;

namespace KikuCaption.Audio.Tests;

public class AudioFormatConverterTests
{
    private static byte[] FloatBytes(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            BitConverter.GetBytes(samples[i]).CopyTo(bytes, i * 4);
        }

        return bytes;
    }

    [Fact]
    public void Resamples48kStereoToApprox16kMono()
    {
        var source = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var converter = new AudioFormatConverter(source);

        // 1 second of 48 kHz stereo silence -> ~16000 mono int16 samples.
        var input = new byte[48000 * 2 * 4];
        byte[] output = converter.Convert(input);

        int outputSamples = output.Length / 2;
        Assert.InRange(outputSamples, 15600, 16400);
        Assert.Equal(0, output.Length % 2); // whole int16 samples
    }

    [Fact]
    public void DownmixesChannelsByAveraging()
    {
        var source = WaveFormat.CreateIeeeFloatWaveFormat(16000, 2); // no resample, just downmix
        var converter = new AudioFormatConverter(source);

        // frame 1: (1.0, -1.0) -> avg 0 ; frame 2: (0.5, 0.5) -> avg 0.5 -> ~16384
        byte[] output = converter.Convert(FloatBytes(1.0f, -1.0f, 0.5f, 0.5f));

        Assert.Equal(4, output.Length); // 2 mono int16 samples
        short s0 = BitConverter.ToInt16(output, 0);
        short s1 = BitConverter.ToInt16(output, 2);
        Assert.InRange(s0, -5, 5);
        Assert.InRange(s1, 16300, 16460);
    }

    [Fact]
    public void ClampsOutOfRangeSamplesToInt16Limits()
    {
        var source = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1); // mono, no resample
        var converter = new AudioFormatConverter(source);

        byte[] output = converter.Convert(FloatBytes(2.0f, -2.0f));

        Assert.Equal(4, output.Length);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(output, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(output, 2));
    }
}
