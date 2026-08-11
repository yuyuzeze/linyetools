using KikuCaption.Core.Diagnostics;

namespace KikuCaption.Recording.FFmpeg;

/// <summary>
/// Locates ffmpeg.exe / ffprobe.exe for the recording module. This is a thin façade over the
/// shared <see cref="FFmpegResolver"/> in Core — recording, the environment check and preflight
/// all resolve FFmpeg through that one resolver, so they can never disagree (UI-R1 §6).
/// The public API is preserved for existing callers (App composition, screen recorder).
/// </summary>
public static class FFmpegLocator
{
    public static string? LocateFFmpeg(string? configuredPath, string baseDirectory)
        => FFmpegResolver.Resolve(configuredPath, baseDirectory).FFmpegPath;

    public static string? LocateFFprobe(string ffmpegPath)
        => FFmpegResolver.ResolveFFprobeBeside(ffmpegPath);
}
