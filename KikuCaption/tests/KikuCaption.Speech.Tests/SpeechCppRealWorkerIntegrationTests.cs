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
    /// <summary>Walk up from the test bin dir to an ancestor that contains <paramref name="segments"/>.</summary>
    private static string? WalkUp(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // Prefer the bundled copy the app actually ships (tools/speech, models/speech); fall back to a
    // per-user speech.cpp install if present.
    private static string? LocateExe()
    {
        var bundled = WalkUp("tools", "speech", "speech.exe");
        if (bundled is not null)
        {
            return bundled;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(localApp, "Programs", "speech.cpp", "current", "speech.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? LocateModel()
    {
        var bundledDir = WalkUp("models", "speech");
        if (bundledDir is not null)
        {
            var hit = Directory.EnumerateFiles(bundledDir, "*Haas*ja*.gguf", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var modelsDir = Path.Combine(localApp, "speech.cpp", "models");
        return Directory.Exists(modelsDir)
            ? Directory.EnumerateFiles(modelsDir, "*Haas*ja*.gguf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
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
        var model = LocateModel();
        if (exe is null || model is null)
        {
            return; // speech.cpp engine/model not available on this machine — skip.
        }

        var options = new SpeechCppWorkerOptions { ExecutablePath = exe, Device = "cpu" };
        var worker = new ProcessSpeechCppWorker(options, NullLogger<ProcessSpeechCppWorker>.Instance);
        await using var recognizer = new SpeechCppSpeechRecognizer(worker, NullLogger<SpeechCppSpeechRecognizer>.Instance);

        await recognizer.InitializeAsync(new SpeechOptions
        {
            Engine = SpeechEngine.SpeechCpp,
            Model = model, // load the bundled model by absolute path (as the shipped app does)
            Language = "ja",
            InitializeTimeout = TimeSpan.FromMinutes(2)
        }, CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
