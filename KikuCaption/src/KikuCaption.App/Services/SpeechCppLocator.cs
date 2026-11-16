using System.IO;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>
/// Locates the native <c>speech.exe</c> and an optional bundled model, always relative to the app's
/// own directory: <c>&lt;baseDir&gt;/tools/speech/speech.exe</c> and
/// <c>&lt;baseDir&gt;/models/speech/*.gguf</c>. The build copies <c>tools/speech</c> and
/// <c>models/speech</c> into the output, so the same exe-relative paths work in dev and when
/// published — no walk-up to the source repo, no per-user install. Returns null when not present,
/// and the engine factory then keeps the Whisper-only path. Logs only the source/category.
/// </summary>
public static class SpeechCppLocator
{
    private const string ExeName = "speech.exe";

    public static string? LocateExecutable(string? explicitPath, string? baseDirectory = null, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            logger?.LogInformation("speech.cpp located from explicit path.");
            return explicitPath;
        }

        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var bundled = Path.Combine(baseDir, "tools", "speech", ExeName);
        if (File.Exists(bundled))
        {
            logger?.LogInformation("speech.cpp located in the app's tools/speech folder.");
            return bundled;
        }

        logger?.LogInformation("speech.cpp not found next to the app; the speech.cpp engine will be unavailable.");
        return null;
    }

    /// <summary>
    /// A model file bundled under <c>&lt;baseDir&gt;/models/speech</c>, if exactly one (or a
    /// <paramref name="nameHint"/> match) is present. Loaded by absolute path, independent of any
    /// per-user speech.cpp model store. Returns null when none is bundled.
    /// </summary>
    public static string? LocateBundledModel(string? baseDirectory, string? nameHint = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        var modelsDir = Path.Combine(baseDir, "models", "speech");
        if (!Directory.Exists(modelsDir))
        {
            return null;
        }

        string[] ggufs;
        try
        {
            ggufs = Directory.GetFiles(modelsDir, "*.gguf", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        if (ggufs.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(nameHint))
        {
            var token = nameHint.Split('-', '.', '/', '\\')[0];
            var match = ggufs.FirstOrDefault(f =>
                Path.GetFileName(f).Contains(token, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return ggufs.Length == 1 ? ggufs[0] : null;
    }
}
