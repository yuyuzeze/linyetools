using KikuCaption.Audio.Wav;
using NAudio.Wave;
using Xunit;

namespace KikuCaption.Audio.Tests;

public class WavFileWriterTests
{
    [Fact]
    public void WritesValidRecognitionFormatWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiku_wav_{Guid.NewGuid():N}.wav");
        try
        {
            // 16000 samples * 2 bytes = 1 second of mono int16 audio.
            var oneSecond = new byte[16000 * 2];
            using (var writer = new WavFileWriter(path))
            {
                writer.Write(oneSecond);
                Assert.Equal(oneSecond.Length, writer.BytesWritten);
            }

            using var reader = new WaveFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(oneSecond.Length, reader.Length);
            Assert.InRange(reader.TotalTime.TotalMilliseconds, 990, 1010);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreatesMissingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"kiku_wavdir_{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "out.wav");
        try
        {
            using (var writer = new WavFileWriter(path))
            {
                writer.Write(new byte[32]);
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
