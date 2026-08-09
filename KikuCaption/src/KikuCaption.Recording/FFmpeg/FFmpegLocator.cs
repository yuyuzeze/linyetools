namespace KikuCaption.Recording.FFmpeg;

/// <summary>
/// Locates ffmpeg.exe / ffprobe.exe. Order (PROJECT.md 5.3, M5 规则):
/// 1) configured path, 2) project-local <c>tools/ffmpeg</c> (searched upward from base dir),
/// 3) PATH. Never hard-codes a machine-specific absolute path.
/// </summary>
public static class FFmpegLocator
{
    public static string? LocateFFmpeg(string? configuredPath, string baseDirectory)
        => Locate("ffmpeg.exe", configuredPath, baseDirectory);

    public static string? LocateFFprobe(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, "ffprobe.exe");
        return File.Exists(candidate) ? candidate : Locate("ffprobe.exe", null, directory);
    }

    private static string? Locate(string fileName, string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "ffmpeg", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

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
