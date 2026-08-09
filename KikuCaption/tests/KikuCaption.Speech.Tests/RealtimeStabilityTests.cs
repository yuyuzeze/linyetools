using System.Diagnostics;
using System.Globalization;
using KikuCaption.Audio.Capture;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// Long-running real-time captioning stability run (Milestone 3). Disabled by default; set
/// KIKU_RT_MINUTES to a positive number (and KIKU_REALMODEL=1). Plays a continuous quiet tone so
/// the pipeline runs continuously, and samples memory / CPU / RTF / counts / queue every 30 s.
/// Results are written to %TEMP%\kiku_rt_stability.txt.
/// </summary>
[Trait("Category", "Stability")]
public class RealtimeStabilityTests
{
    private readonly ITestOutputHelper _output;

    public RealtimeStabilityTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Realtime_LongRun_StableMemory()
    {
        int minutes = Read("KIKU_RT_MINUTES");
        if (minutes <= 0 || !RealModelSupport.Enabled)
        {
            _output.WriteLine("[SKIPPED] 需要 KIKU_REALMODEL=1 且 KIKU_RT_MINUTES>0。");
            return;
        }

        var located = RealModelSupport.Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir)) { _output.WriteLine("[SKIPPED] 无 venv/模型"); return; }
        try { using var probe = new WasapiLoopbackCapture(); }
        catch (Exception ex) { _output.WriteLine("[SKIPPED] 无音频设备：" + ex.Message); return; }

        var lines = new List<string>();
        void Log(string s) { lines.Add(s); _output.WriteLine(s); }

        var capture = new WasapiLoopbackAudioCaptureService(NullLogger<WasapiLoopbackAudioCaptureService>.Instance);
        await using var pipeline = new RealtimeCaptionPipeline(
            RealModelSupport.RecognizerFactory(located.Value.Options),
            new ProgressiveCaptionOptions { PartialIntervalMs = 800, MaxSentenceSeconds = 10, MaxWaitSeconds = 15 },
            NullLogger<RealtimeCaptionPipeline>.Instance);

        var main = Process.GetCurrentProcess();
        long startMain = main.WorkingSet64;
        long startPy = PythonWorkingSet();
        var pyCpuStart = PythonCpuTime();
        var sw = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource();
        await pipeline.StartAsync(capture.CaptureAsync(cts.Token), "zh", CancellationToken.None);

        using var waveOut = new WaveOutEvent();
        var tone = new SignalGenerator(44100, 2) { Gain = 0.02, Frequency = 330, Type = SignalGeneratorType.Sin };
        waveOut.Init(tone);
        waveOut.Play();

        Log($"START main={Mb(startMain)}MB python={Mb(startPy)}MB minutes={minutes}");
        var total = TimeSpan.FromMinutes(minutes);
        while (sw.Elapsed < total)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            var m = pipeline.CurrentMetrics;
            Log($"t={sw.Elapsed:mm\\:ss} main={Mb(main.WorkingSet64)}MB python={Mb(PythonWorkingSet())}MB " +
                $"RTF={m.Rtf:0.00} infer={m.LastInferenceMs}ms partial={m.PartialCount} final={m.FinalCount} " +
                $"queue={m.QueueDepthMs}ms skipped={m.SkippedCycles}");
        }

        waveOut.Stop();
        cts.Cancel();
        await pipeline.StopAsync();
        await capture.DisposeAsync();

        var pyCpuEnd = PythonCpuTime();
        double cpuPct = sw.Elapsed.TotalSeconds > 0
            ? (pyCpuEnd - pyCpuStart).TotalSeconds / (sw.Elapsed.TotalSeconds * Environment.ProcessorCount) * 100
            : 0;

        long endMain = Process.GetCurrentProcess().WorkingSet64;
        Log($"END main={Mb(endMain)}MB python-cpu%≈{cpuPct:0.0} runtime={sw.Elapsed:mm\\:ss}");

        var logPath = Path.Combine(Path.GetTempPath(), "kiku_rt_stability.txt");
        await File.WriteAllLinesAsync(logPath, lines);

        // No unbounded growth on the .NET side (worker runs in a separate process).
        Assert.True(endMain < startMain + 200L * 1024 * 1024, $"main grew {Mb(startMain)}->{Mb(endMain)} MB");
    }

    private static int Read(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long PythonWorkingSet()
    {
        long total = 0;
        foreach (var p in Process.GetProcessesByName("python"))
        {
            try { total += p.WorkingSet64; } catch { }
            finally { p.Dispose(); }
        }

        return total;
    }

    private static TimeSpan PythonCpuTime()
    {
        var total = TimeSpan.Zero;
        foreach (var p in Process.GetProcessesByName("python"))
        {
            try { total += p.TotalProcessorTime; } catch { }
            finally { p.Dispose(); }
        }

        return total;
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);
}
