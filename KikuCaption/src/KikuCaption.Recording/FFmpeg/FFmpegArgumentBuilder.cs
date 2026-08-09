using System.Globalization;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;

namespace KikuCaption.Recording.FFmpeg;

/// <summary>
/// Builds the FFmpeg argument list (never a shell string) for gdigrab video + raw-PCM pipe audio
/// → H.264 + AAC MP4. Pure and unit-testable. Window titles/paths are passed as single argument
/// tokens, so spaces/special characters can never inject a command (PROJECT.md 13, M5 安全).
/// </summary>
public static class FFmpegArgumentBuilder
{
    public static IReadOnlyList<string> Build(RecordingOptions options, string? audioPipeName)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            throw new RecordingException("no_output", "缺少输出路径。");
        }

        bool audio = options.IncludeSystemAudio && !string.IsNullOrEmpty(audioPipeName);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y" };

        // Video input: GDI screen/window grabber.
        args.Add("-thread_queue_size");
        args.Add("1024");
        args.Add("-f");
        args.Add("gdigrab");
        args.Add("-framerate");
        args.Add(Int(options.FrameRate));
        args.Add("-i");
        if (options.CaptureType == CaptureTargetType.Window)
        {
            if (string.IsNullOrWhiteSpace(options.TargetTitle))
            {
                throw new RecordingException("no_target", "窗口捕获需要窗口标题。");
            }

            args.Add($"title={options.TargetTitle}"); // single token — no shell, no injection
        }
        else
        {
            args.Add("desktop");
        }

        // Audio input: raw PCM from the named pipe (exact format match with the sink).
        if (audio)
        {
            args.Add("-thread_queue_size");
            args.Add("1024");
            args.Add("-f");
            args.Add("s16le");
            args.Add("-ar");
            args.Add(Int(options.AudioSampleRate));
            args.Add("-ac");
            args.Add(Int(options.AudioChannels));
            // Raw PCM: derive timestamps from the sample rate (wallclock stamping clusters pipe
            // reads). Sync is achieved by the continuous timeline + zero-basing both stream starts
            // below (PROJECT.md M5 修正).
            args.Add("-i");
            args.Add($@"\\.\pipe\{audioPipeName}");
        }

        // Mapping + encoders.
        args.Add("-map");
        args.Add("0:v:0");
        if (audio)
        {
            args.Add("-map");
            args.Add("1:a:0");
        }

        args.Add("-c:v");
        args.Add(options.Encoder);
        if (options.Encoder == "libx264")
        {
            args.Add("-preset");
            args.Add("veryfast");
            args.Add("-crf");
            args.Add("23");
        }
        else if (options.Encoder == "h264_qsv")
        {
            args.Add("-global_quality");
            args.Add("25");
        }

        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-r");
        args.Add(Int(options.FrameRate));

        if (audio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("96k");
            args.Add("-ar");
            args.Add(Int(options.AudioSampleRate));
            args.Add("-ac");
            args.Add(Int(options.AudioChannels));
        }

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(options.OutputPath);

        return args;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
