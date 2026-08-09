using NAudio.Wave;

namespace KikuCaption.Audio.Conversion;

/// <summary>
/// Down-mixes an N-channel float sample stream to mono by averaging all channels of each
/// frame. Works for any channel count (WASAPI loopback is usually stereo, but can differ).
/// </summary>
public sealed class MonoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public MonoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamplesNeeded = count * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        int sourceRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceRead / _sourceChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            int baseIndex = frame * _sourceChannels;
            float sum = 0f;
            for (int channel = 0; channel < _sourceChannels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / _sourceChannels;
        }

        return framesRead;
    }
}
