using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Speech.SpeechCpp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// End-to-end check against the REAL speech.exe + Haas model. Silently passes (returns) when the
/// executable or the model is not installed, so it is safe on machines without speech.cpp. When
/// present, it proves the real process transport + protocol + single-utterance mapping work:
/// ready → chunks → transcribe → end → one FinalCandidate.
/// </summary>
public class SpeechCppRealWorkerIntegrationTests
{
    private static string? LocateExe()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localApp, "Programs", "speech.cpp", "current", "speech.exe");
        return File.Exists(path) ? path : null;
    }

    private static bool HaasModelPresent()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var modelsDir = Path.Combine(localApp, "speech.cpp", "models");
        return Directory.Exists(modelsDir)
            && Directory.EnumerateFiles(modelsDir, "*Haas*ja*.gguf", SearchOption.AllDirectories).Any();
    }

    private static async IAsyncEnumerable<AudioChunk> Silence(double seconds)
    {
        int bytes = (int)(seconds * 16000) * 2;
        await Task.Yield();
        yield return new AudioChunk(new byte[bytes], TimeSpan.Zero, TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task RealWorker_Haas_Initializes_AndMapsEndToSingleFinalCandidate()
    {
        var exe = LocateExe();
        if (exe is null || !HaasModelPresent())
        {
            return; // speech.cpp not installed on this machine — skip.
        }

        var options = new SpeechCppWorkerOptions { ExecutablePath = exe, Device = "cpu" };
        var worker = new ProcessSpeechCppWorker(options, NullLogger<ProcessSpeechCppWorker>.Instance);
        await using var recognizer = new SpeechCppSpeechRecognizer(worker, NullLogger<SpeechCppSpeechRecognizer>.Instance);

        await recognizer.InitializeAsync(new SpeechOptions
        {
            Engine = SpeechEngine.SpeechCpp,
            Model = "haas-ja-v1.0",
            Language = "ja",
            InitializeTimeout = TimeSpan.FromMinutes(1)
        }, CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var u in recognizer.RecognizeAsync(Silence(1.0), cts.Token))
        {
            updates.Add(u);
        }

        // One recognition request -> exactly one final-candidate (text may be empty for silence).
        var update = Assert.Single(updates);
        Assert.Equal(TranscriptUpdateKind.FinalCandidate, update.Kind);
        Assert.NotNull(update.Text);
    }
}
