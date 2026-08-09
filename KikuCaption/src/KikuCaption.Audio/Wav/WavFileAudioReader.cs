using System.Runtime.CompilerServices;
using KikuCaption.Audio.Conversion;
using KikuCaption.Core.Models;
using NAudio.Wave;

namespace KikuCaption.Audio.Wav;

/// <summary>
/// Reads a WAV file and yields it as a stream of <see cref="AudioChunk"/> normalized to the
/// recognition format (16 kHz / mono / int16), reusing the Milestone 1 converter. Used by the
/// Milestone 2 "recognize an existing WAV" verification path.
/// </summary>
public static class WavFileAudioReader
{
    public static async IAsyncEnumerable<AudioChunk> ReadAsync(
        string filePath,
        int maxChunkBytes = 32000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new WaveFileReader(filePath);
        var converter = new AudioFormatConverter(reader.WaveFormat);

        var sourceBuffer = new byte[Math.Max(4096, reader.WaveFormat.AverageBytesPerSecond)];
        long producedSamples = 0;
        int read;

        while ((read = reader.Read(sourceBuffer, 0, sourceBuffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pcm = converter.Convert(sourceBuffer.AsSpan(0, read));
            int offset = 0;
            while (offset < pcm.Length)
            {
                int take = Math.Min(maxChunkBytes, pcm.Length - offset);
                if (take % 2 != 0)
                {
                    take -= 1;
                }

                if (take <= 0)
                {
                    break;
                }

                int sampleCount = take / 2;
                var timestamp = TimeSpan.FromSeconds((double)producedSamples / AudioFormatConverter.TargetSampleRate);
                var duration = TimeSpan.FromSeconds((double)sampleCount / AudioFormatConverter.TargetSampleRate);
                producedSamples += sampleCount;

                yield return new AudioChunk(pcm.AsMemory(offset, take), timestamp, duration);
                offset += take;
            }

            await Task.Yield();
        }
    }
}
