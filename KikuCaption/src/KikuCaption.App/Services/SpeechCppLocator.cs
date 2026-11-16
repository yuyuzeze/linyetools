using System.IO;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>
/// Locates the native <c>speech.exe</c> for the speech.cpp backend. Prefers the per-user install
/// (<c>%LOCALAPPDATA%\Programs\speech.cpp\current\speech.exe</c>), then an explicit override, then
/// the PATH. Returns null when not found — the app then keeps the Whisper-only path (the engine
/// factory falls back). Logs only the source/category, never scanned paths.
/// </summary>
public static class SpeechCppLocator
{
    public static string? LocateExecutable(string? explicitPath, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            logger?.LogInformation("speech.cpp located from explicit path.");
            return explicitPath;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var perUser = Path.Combine(localApp, "Programs", "speech.cpp", "current", "speech.exe");
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
                var candidate = Path.Combine(dir, "speech.exe");
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
