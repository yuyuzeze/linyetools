using KikuCaption.Core.Interfaces;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.Speech.Tests;

/// <summary>Shared helpers for gated real-model tests (KIKU_REALMODEL=1).</summary>
internal static class RealModelSupport
{
    public static bool Enabled => Environment.GetEnvironmentVariable("KIKU_REALMODEL") == "1";

    public static string? ChineseWav => Environment.GetEnvironmentVariable("KIKU_ZH_WAV");

    public static (WhisperWorkerOptions Options, string ModelDir)? Locate()
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

    public static Func<ISpeechRecognizer> RecognizerFactory(WhisperWorkerOptions options)
        => () => new PythonSpeechRecognizer(
            new ProcessWhisperWorker(options, NullLogger<ProcessWhisperWorker>.Instance),
            NullLogger<PythonSpeechRecognizer>.Instance);
}
