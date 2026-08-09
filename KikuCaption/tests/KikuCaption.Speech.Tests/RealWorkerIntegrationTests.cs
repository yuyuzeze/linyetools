using System.Diagnostics;
using System.Runtime.CompilerServices;
using KikuCaption.Audio.Wav;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// End-to-end tests against the REAL Python worker + faster-whisper small model. Disabled by
/// default (set <c>KIKU_REALMODEL=1</c> to run) so the normal suite stays fast and venv/model
/// free. The Chinese-speech test additionally needs a 16 kHz mono WAV path in <c>KIKU_ZH_WAV</c>.
/// </summary>
[Trait("Category", "RealModel")]
public class RealWorkerIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RealWorkerIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("KIKU_REALMODEL") == "1";

    private static (WhisperWorkerOptions Options, string ModelDir)? Locate()
    {
        var located = WhisperWorkerLocator.TryLocate(AppContext.BaseDirectory);
        if (located is null)
        {
            return null;
        }

        var (python, script) = located.Value;
        if (!File.Exists(python) || !File.Exists(script))
        {
            return null;
        }

        var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)))!;
        var modelDir = Path.Combine(repoRoot, "models", "whisper");

        return (new WhisperWorkerOptions
        {
            PythonExecutable = python,
            WorkerScript = script,
            ModelCacheDirectory = modelDir
        }, modelDir);
    }

    private static PythonSpeechRecognizer CreateRecognizer(WhisperWorkerOptions options)
        => new(new ProcessWhisperWorker(options, NullLogger<ProcessWhisperWorker>.Instance),
            NullLogger<PythonSpeechRecognizer>.Instance);

    private static async IAsyncEnumerable<AudioChunk> Tone(double seconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int sampleRate = 16000;
        int total = (int)(seconds * sampleRate);
        const int perChunk = 8000;
        int done = 0;

        while (done < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(perChunk, total - done);
            var bytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                double s = 0.05 * Math.Sin(2 * Math.PI * 220 * (done + i) / sampleRate);
                BitConverter.GetBytes((short)(s * short.MaxValue)).CopyTo(bytes, i * 2);
            }

            yield return new AudioChunk(bytes,
                TimeSpan.FromSeconds((double)done / sampleRate), TimeSpan.FromSeconds((double)n / sampleRate));
            done += n;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task RealWorker_LoadsModelOnce_CleanRoundTrip_NoOrphan()
    {
        if (!Enabled)
        {
            _output.WriteLine("[SKIPPED] 设置 KIKU_REALMODEL=1 以运行真实模型集成测试。");
            return;
        }

        var located = Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir))
        {
            _output.WriteLine("[SKIPPED] 未找到 venv/worker 或模型未下载。");
            return;
        }

        var worker = new ProcessWhisperWorker(located.Value.Options, NullLogger<ProcessWhisperWorker>.Instance);
        var recognizer = new PythonSpeechRecognizer(worker, NullLogger<PythonSpeechRecognizer>.Instance);

        var stopwatch = Stopwatch.StartNew();
        await recognizer.InitializeAsync(new SpeechOptions
        {
            Language = "ja",
            ModelCacheDirectory = located.Value.Options.ModelCacheDirectory,
            InitializeTimeout = TimeSpan.FromMinutes(3)
        }, CancellationToken.None);
        _output.WriteLine($"ready (model loaded) in {stopwatch.ElapsedMilliseconds} ms");

        // Two recognitions on the same worker (a tone is not speech, so 0 segments is fine —
        // we assert the mechanics, not the content).
        int run1 = 0, run2 = 0;
        await foreach (var u in recognizer.RecognizeAsync(Tone(6), CancellationToken.None))
        {
            Assert.True(u.EndTime >= u.StartTime);
            run1++;
        }

        await foreach (var _ in recognizer.RecognizeAsync(Tone(2), CancellationToken.None))
        {
            run2++;
        }

        _output.WriteLine($"round-trips completed (run1={run1}, run2={run2} updates)");

        await recognizer.DisposeAsync();
        Assert.True(worker.HasExited, "worker should have exited after dispose (no orphan).");
    }

    [Fact]
    public async Task RealWorker_RecognizesChineseSpeech_ReadableText()
    {
        if (!Enabled)
        {
            _output.WriteLine("[SKIPPED] 设置 KIKU_REALMODEL=1 以运行。");
            return;
        }

        var wavPath = Environment.GetEnvironmentVariable("KIKU_ZH_WAV");
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
        {
            _output.WriteLine("[SKIPPED] 未提供中文测试音频（设置 KIKU_ZH_WAV 指向 16k mono WAV）。");
            return;
        }

        var located = Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir))
        {
            _output.WriteLine("[SKIPPED] 未找到 venv/worker 或模型未下载。");
            return;
        }

        await using var recognizer = CreateRecognizer(located.Value.Options);
        await recognizer.InitializeAsync(new SpeechOptions
        {
            Language = "zh",
            ModelCacheDirectory = located.Value.Options.ModelCacheDirectory,
            InitializeTimeout = TimeSpan.FromMinutes(3)
        }, CancellationToken.None);

        var finals = new List<TranscriptUpdate>();
        await foreach (var update in recognizer.RecognizeAsync(WavFileAudioReader.ReadAsync(wavPath!), CancellationToken.None))
        {
            if (update.Kind == TranscriptUpdateKind.FinalCandidate)
            {
                finals.Add(update);
            }
        }

        var text = string.Concat(finals.Select(f => f.Text));
        _output.WriteLine($"recognized zh text: {text}");
        _output.WriteLine($"final segments: {finals.Count}, first span: " +
            (finals.Count > 0 ? $"{finals[0].StartTime}-{finals[0].EndTime}" : "n/a"));

        Assert.NotEmpty(finals);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(text, c => c >= '一' && c <= '鿿'); // contains CJK
        Assert.All(finals, f => Assert.True(f.EndTime >= f.StartTime));
    }
}
