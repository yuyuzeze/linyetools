using KikuCaption.Audio.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Audio.Tests;

/// <summary>
/// Real-device integration test for the full capture pipeline (WASAPI loopback → 16k/mono/int16
/// conversion → WAV). It plays a short test tone through the default output so the loopback has
/// real audio to record, then asserts the WAV format, duration and that actual signal was
/// captured. If no audio endpoint is available (headless CI) it returns early; the report
/// records that case as "未验证".
/// </summary>
[Trait("Category", "Integration")]
public class LoopbackCaptureIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public LoopbackCaptureIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool TryOpenLoopbackDevice(out string reason)
    {
        try
        {
            using var capture = new WasapiLoopbackCapture();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    [Fact]
    public async Task Capture_RealDevice_WithTonePlayback_ProducesValidWavWithSignal()
    {
        if (!TryOpenLoopbackDevice(out var reason))
        {
            _output.WriteLine($"[SKIPPED] 无可用系统音频输出设备：{reason}");
            return;
        }

        var recorder = new SystemAudioWavRecorder(
            () => new WasapiLoopbackAudioCaptureService(NullLogger<WasapiLoopbackAudioCaptureService>.Instance),
            NullLogger<SystemAudioWavRecorder>.Instance);

        var path = Path.Combine(Path.GetTempPath(), $"kiku_integration_{Guid.NewGuid():N}.wav");

        try
        {
            await recorder.StartAsync(path);

            // Render a 440 Hz tone to the default endpoint; the loopback should capture it.
            try
            {
                using var waveOut = new WaveOutEvent();
                var tone = new SignalGenerator(44100, 2)
                {
                    Gain = 0.3,
                    Frequency = 440,
                    Type = SignalGeneratorType.Sin
                };
                waveOut.Init(tone);
                waveOut.Play();
                await Task.Delay(TimeSpan.FromSeconds(2));
                waveOut.Stop();
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[SKIPPED] 无法播放测试音（可能没有输出设备）：{ex.Message}");
                await recorder.StopAsync();
                return;
            }

            await recorder.StopAsync();
            Assert.Equal(AudioRecorderState.Stopped, recorder.State);

            using var reader = new WaveFileReader(path);
            short peak = ReadPeakAmplitude(reader);

            _output.WriteLine(
                $"WAV: {reader.WaveFormat.SampleRate} Hz, {reader.WaveFormat.Channels} ch, " +
                $"{reader.WaveFormat.BitsPerSample}-bit, duration {reader.TotalTime.TotalSeconds:0.00}s, " +
                $"bytes {reader.Length}, peak amplitude {peak}");

            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.True(reader.Length > 0, "captured no audio data");
            Assert.True(reader.TotalTime > TimeSpan.FromSeconds(1),
                $"expected > 1s of audio, got {reader.TotalTime.TotalSeconds:0.00}s");
            Assert.True(peak > 100, $"captured only near-silence (peak {peak})");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static short ReadPeakAmplitude(WaveFileReader reader)
    {
        var buffer = new byte[reader.Length];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = reader.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        short peak = 0;
        for (int i = 0; i + 1 < total; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            int magnitude = Math.Abs((int)sample);
            if (magnitude > peak)
            {
                peak = (short)Math.Min(magnitude, short.MaxValue);
            }
        }

        return peak;
    }
}
