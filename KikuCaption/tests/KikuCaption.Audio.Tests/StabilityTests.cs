using System.Diagnostics;
using System.Globalization;
using KikuCaption.Audio.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Audio.Tests;

/// <summary>
/// Long-running memory-stability check for continuous capture. Disabled by default; set the
/// environment variable <c>KIKU_STABILITY_MINUTES</c> to a positive number to run it, e.g.
/// <c>KIKU_STABILITY_MINUTES=30 dotnet test --filter Category=Stability</c>.
///
/// It captures continuously (with a very quiet tone so the loopback stays active), samples
/// managed heap and working set every 30 s, and asserts the managed heap does not grow
/// without bound. Results are also written to %TEMP%\kiku_stability.txt.
/// </summary>
[Trait("Category", "Stability")]
public class StabilityTests
{
    private readonly ITestOutputHelper _output;

    public StabilityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Capture_LongRun_NoUnboundedMemoryGrowth()
    {
        var minutes = ReadMinutes();
        if (minutes <= 0)
        {
            _output.WriteLine("[SKIPPED] 设置 KIKU_STABILITY_MINUTES>0 以运行稳定性测试。");
            return;
        }

        try
        {
            using var probe = new WasapiLoopbackCapture();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[SKIPPED] 无可用音频输出设备：{ex.Message}");
            return;
        }

        var logPath = Path.Combine(Path.GetTempPath(), "kiku_stability.txt");
        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            _output.WriteLine(line);
        }

        var recorder = new SystemAudioWavRecorder(
            () => new WasapiLoopbackAudioCaptureService(NullLogger<WasapiLoopbackAudioCaptureService>.Instance),
            NullLogger<SystemAudioWavRecorder>.Instance);
        var wavPath = Path.Combine(Path.GetTempPath(), $"kiku_stability_{Guid.NewGuid():N}.wav");

        long startManaged, startWorkingSet;
        try
        {
            GcCollect();
            startManaged = GC.GetTotalMemory(forceFullCollection: true);
            startWorkingSet = CurrentWorkingSet();
            Log($"START  t=00:00  managed={Mb(startManaged)}MB  ws={Mb(startWorkingSet)}MB  minutes={minutes}");

            await recorder.StartAsync(wavPath);

            using var waveOut = new WaveOutEvent();
            var tone = new SignalGenerator(44100, 2) { Gain = 0.005, Frequency = 440, Type = SignalGeneratorType.Sin };
            waveOut.Init(tone);
            waveOut.Play();

            var stopwatch = Stopwatch.StartNew();
            var total = TimeSpan.FromMinutes(minutes);
            while (stopwatch.Elapsed < total)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                Log($"SAMPLE t={stopwatch.Elapsed:mm\\:ss}  managed={Mb(GC.GetTotalMemory(false))}MB  " +
                    $"ws={Mb(CurrentWorkingSet())}MB  audioBytes={recorder.BytesWritten}");
            }

            waveOut.Stop();
            await recorder.StopAsync();
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
        }

        GcCollect();
        long endManaged = GC.GetTotalMemory(forceFullCollection: true);
        long endWorkingSet = CurrentWorkingSet();
        Log($"END    managed={Mb(endManaged)}MB  ws={Mb(endWorkingSet)}MB  audioBytes={recorder.BytesWritten}");

        await File.WriteAllLinesAsync(logPath, lines);

        Assert.True(recorder.BytesWritten > 0, "整段运行没有捕获到音频。");
        // No unbounded growth: allow generous head-room for JIT/GC warm-up.
        Assert.True(endManaged < startManaged + 80L * 1024 * 1024,
            $"managed heap grew from {Mb(startManaged)}MB to {Mb(endManaged)}MB");
    }

    private static int ReadMinutes()
    {
        var raw = Environment.GetEnvironmentVariable("KIKU_STABILITY_MINUTES");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 0;
    }

    private static void GcCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static long CurrentWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
