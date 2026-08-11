using System.IO;

namespace KikuCaption.Core.Diagnostics;

/// <summary>
/// The single source of truth for locating the FFmpeg tool pair (PROJECT.md 5.3, UI-R1 §6).
///
/// Both the environment check, the Milestone-7 preflight and the recording module must resolve
/// FFmpeg through this one resolver so they can never disagree ("recording finds it, the
/// environment check says it is missing"). It resolves <c>ffmpeg.exe</c> and its paired
/// <c>ffprobe.exe</c> in a fixed order and returns the full resolved paths — it never runs the
/// executables (that is the probe's job) and never hard-codes a machine-specific path.
///
/// Search order:
/// <list type="number">
///   <item>the user-configured path (if it points at an existing file);</item>
///   <item>the published app's <c>tools/ffmpeg</c>, then the repository's <c>tools/ffmpeg</c>
///     found by walking up from the base directory (both covered by the same upward walk);</item>
///   <item>the system PATH.</item>
/// </list>
/// </summary>
public static class FFmpegResolver
{
    public const string FFmpegExe = "ffmpeg.exe";
    public const string FFprobeExe = "ffprobe.exe";

    /// <summary>
    /// Resolves the ffmpeg/ffprobe pair. <paramref name="configuredFFmpegPath"/> is the optional
    /// user-configured ffmpeg path; <paramref name="baseDirectory"/> is where the upward
    /// <c>tools/ffmpeg</c> walk starts (normally the app base directory).
    /// </summary>
    public static FFmpegResolution Resolve(string? configuredFFmpegPath, string baseDirectory)
    {
        var ffmpeg = Locate(FFmpegExe, configuredFFmpegPath, baseDirectory);

        // ffprobe must be the mate of the resolved ffmpeg: look right beside it first so the pair
        // always comes from the same install, then fall back to the same ordered search.
        string? ffprobe = null;
        if (ffmpeg is not null)
        {
            var directory = Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrEmpty(directory))
            {
                var beside = Path.Combine(directory, FFprobeExe);
                ffprobe = File.Exists(beside) ? beside : Locate(FFprobeExe, null, baseDirectory);
            }
        }
        else
        {
            ffprobe = Locate(FFprobeExe, null, baseDirectory);
        }

        return new FFmpegResolution(ffmpeg, ffprobe);
    }

    /// <summary>Resolves ffprobe given an already-resolved ffmpeg path (pair helper).</summary>
    public static string? ResolveFFprobeBeside(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var beside = Path.Combine(directory, FFprobeExe);
        return File.Exists(beside) ? beside : Locate(FFprobeExe, null, directory);
    }

    private static string? Locate(string fileName, string? configuredPath, string baseDirectory)
    {
        // 1) user-configured explicit path (spaces in the path are fine — no shell involved).
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        // 2)+3) published tools/ffmpeg, then the repo's tools/ffmpeg, by walking up.
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(baseDirectory);
        }
        catch
        {
            directory = null;
        }

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "ffmpeg", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        // 4) system PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }

        return null;
    }
}

/// <summary>
/// The resolved FFmpeg tool pair. A missing member is <c>null</c>. <see cref="IsComplete"/> means
/// both tools were found (recording needs the pair); a lone ffmpeg is reported so the UI can warn.
/// </summary>
public sealed record FFmpegResolution(string? FFmpegPath, string? FFprobePath)
{
    public bool HasFFmpeg => FFmpegPath is not null;
    public bool HasFFprobe => FFprobePath is not null;
    public bool IsComplete => HasFFmpeg && HasFFprobe;
}
