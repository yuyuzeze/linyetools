using System.IO;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>
/// Locates the native <c>speech.exe</c> (and an optional bundled model) for the speech.cpp backend.
/// Search order mirrors the FFmpeg resolver so the app is self-contained and needs no separate
/// speech.cpp install: an explicit override, then a bundled <c>tools/speech</c> found by walking up
/// from the app base directory (dev: the repo's <c>tools/speech</c>; published: next to the exe),
/// then the per-user install, then PATH. The engine binaries live in <c>tools/speech</c>; bundled
/// models live under <c>models/speech</c> (alongside the Whisper <c>models/whisper</c> cache) and are
/// loaded by absolute path. Returns null when not found — the engine factory then keeps the
/// Whisper-only path. Logs only the source/category, never scanned paths.
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

        var bundled = WalkUpForToolsSpeech(baseDirectory, ExeName);
        if (bundled is not null)
        {
            logger?.LogInformation("speech.cpp located in the bundled tools/speech folder.");
            return bundled;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perUser = Path.Combine(localApp, "Programs", "speech.cpp", "current", ExeName);
        if (File.Exists(perUser))
        {
            logger?.LogInformation("speech.cpp located in the per-user install.");
            return perUser;
        }

        var onPath = ProbePath();
        if (onPath is not null)
        {
            logger?.LogInformation("speech.cpp located on PATH.");
            return onPath;
        }

        logger?.LogInformation("speech.cpp not found; the speech.cpp engine will be unavailable.");
        return null;
    }

    /// <summary>
    /// A model file bundled under <c>models/speech</c>, if exactly one (or a <paramref name="nameHint"/>
    /// match) is present. Lets the app ship a self-contained model that is loaded by absolute path,
    /// independent of the per-user speech.cpp model store. Returns null when none is bundled.
    /// </summary>
    public static string? LocateBundledModel(string? baseDirectory, string? nameHint = null)
    {
        var modelsDir = WalkUpForDirectory(baseDirectory, "models", "speech");
        if (modelsDir is null)
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
            // Prefer a file whose name contains a distinctive token of the hint (e.g. "haas").
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

    private static string? WalkUpForToolsSpeech(string? baseDirectory, string fileName)
    {
        var dir = WalkUpForDirectory(baseDirectory, "tools", "speech");
        if (dir is null)
        {
            return null;
        }

        var candidate = Path.Combine(dir, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Walks up from <paramref name="baseDirectory"/> (the app base dir by default) looking for an
    /// ancestor that contains <paramref name="segments"/> as a sub-path, and returns that sub-path's
    /// full path. Used to find the repo/publish <c>tools/speech</c> and <c>models/speech</c> folders.
    /// </summary>
    private static string? WalkUpForDirectory(string? baseDirectory, params string[] segments)
    {
        var start = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(start);
        }
        catch
        {
            return null;
        }

        for (int depth = 0; directory is not null && depth < 8; depth++)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? ProbePath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, ExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // malformed PATH entry — ignore
            }
        }

        return null;
    }
}
