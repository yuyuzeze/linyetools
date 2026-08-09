using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KikuCaption.Audio.Conversion;

/// <summary>
/// Streaming converter from an arbitrary WASAPI source format to the recognition format:
/// <b>16 kHz, mono, signed 16-bit little-endian PCM</b> (PROJECT.md 5.2).
///
/// It keeps a single resampler chain alive for the whole capture session so that the
/// anti-aliasing filter state carries across chunks (no clicks at chunk boundaries).
/// The class is deliberately UI- and device-free so it can be unit tested with synthetic
/// input.
/// </summary>
public sealed class AudioFormatConverter
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    private readonly BufferedWaveProvider _source;
    private readonly ISampleProvider _pipeline;
    private float[] _readBuffer = new float[4096];

    public AudioFormatConverter(WaveFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);

        // Feed raw source bytes here; ReadFully=false so Read only returns buffered data
        // (never pads with silence).
        _source = new BufferedWaveProvider(sourceFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = false,
            BufferDuration = TimeSpan.FromSeconds(10)
        };

        ISampleProvider samples = _source.ToSampleProvider();
        if (samples.WaveFormat.Channels != TargetChannels)
        {
            samples = new MonoDownmixSampleProvider(samples);
        }

        _pipeline = samples.WaveFormat.SampleRate == TargetSampleRate
            ? samples
            : new WdlResamplingSampleProvider(samples, TargetSampleRate);
    }

    /// <summary>
    /// Pushes one block of source PCM through the pipeline and returns the resulting
    /// 16 kHz/mono/int16 bytes. May return an empty array if not enough input has
    /// accumulated to produce output yet.
    /// </summary>
    public byte[] Convert(ReadOnlySpan<byte> sourcePcm)
    {
        if (sourcePcm.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        _source.AddSamples(sourcePcm.ToArray(), 0, sourcePcm.Length);

        using var output = new MemoryStream();
        int read;
        do
        {
            read = _pipeline.Read(_readBuffer, 0, _readBuffer.Length);
            for (int i = 0; i < read; i++)
            {
                short sample = FloatToInt16(_readBuffer[i]);
                output.WriteByte((byte)(sample & 0xFF));
                output.WriteByte((byte)((sample >> 8) & 0xFF));
            }
        }
        while (read > 0);

        return output.ToArray();
    }

    private static short FloatToInt16(float value)
    {
        // Clamp to [-1, 1] then scale, avoiding overflow at +1.0.
        if (value > 1f) value = 1f;
        else if (value < -1f) value = -1f;
        return (short)Math.Round(value * short.MaxValue);
    }
}
