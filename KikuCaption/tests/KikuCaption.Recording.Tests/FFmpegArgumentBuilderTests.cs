using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Recording.FFmpeg;
using Xunit;

namespace KikuCaption.Recording.Tests;

public class FFmpegArgumentBuilderTests
{
    private static RecordingOptions ScreenOptions(string encoder = "libx264", bool audio = true) => new()
    {
        CaptureType = CaptureTargetType.Screen,
        OutputPath = @"C:\out dir\meeting.mp4",
        FFmpegPath = @"C:\tools\ffmpeg.exe",
        FrameRate = 15,
        Encoder = encoder,
        IncludeSystemAudio = audio,
        AudioSampleRate = 16000,
        AudioChannels = 1
    };

    private static void AssertOrdered(IReadOnlyList<string> args, string a, string b)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == a && args[i + 1] == b)
            {
                return;
            }
        }

        Assert.Fail($"expected '{a}' immediately before '{b}'");
    }

    [Fact] // 1,3,6,7: screen + 15fps + s16le pcm + aac
    public void Screen_Default_HasGdigrabDesktopPcmAac()
    {
        var args = FFmpegArgumentBuilder.Build(ScreenOptions(), "pipeX");
        AssertOrdered(args, "-f", "gdigrab");
        AssertOrdered(args, "-framerate", "15");
        AssertOrdered(args, "-i", "desktop");
        AssertOrdered(args, "-f", "s16le");
        AssertOrdered(args, "-ar", "16000");
        AssertOrdered(args, "-ac", "1");
        Assert.Contains(@"\\.\pipe\pipeX", args);
        AssertOrdered(args, "-c:a", "aac");
        Assert.Equal(@"C:\out dir\meeting.mp4", args[^1]); // output path is a single token
    }

    [Fact] // 2,9: window title is a single token even with spaces/special chars
    public void Window_TitleIsSingleToken()
    {
        var options = ScreenOptions() with { CaptureType = CaptureTargetType.Window, TargetTitle = "会议 — Teams [1] & x" };
        var args = FFmpegArgumentBuilder.Build(options, "p");
        Assert.Contains("title=会议 — Teams [1] & x", args);
        Assert.DoesNotContain(args, a => a.Contains("cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // 4: QSV params
    public void QuickSync_Params()
    {
        var args = FFmpegArgumentBuilder.Build(ScreenOptions(encoder: "h264_qsv"), "p");
        AssertOrdered(args, "-c:v", "h264_qsv");
        AssertOrdered(args, "-global_quality", "25");
    }

    [Fact] // 5: libx264 params
    public void Libx264_Params()
    {
        var args = FFmpegArgumentBuilder.Build(ScreenOptions(encoder: "libx264"), "p");
        AssertOrdered(args, "-c:v", "libx264");
        AssertOrdered(args, "-preset", "veryfast");
        AssertOrdered(args, "-crf", "23");
    }

    [Fact] // 8: no audio → no audio input/codec
    public void NoAudio_OmitsAudio()
    {
        var args = FFmpegArgumentBuilder.Build(ScreenOptions(audio: false), null);
        Assert.DoesNotContain("s16le", args);
        Assert.DoesNotContain("aac", args);
        Assert.DoesNotContain("1:a:0", args);
    }

    [Fact] // 11: invalid input rejected
    public void Window_WithoutTitle_Throws()
    {
        var options = ScreenOptions() with { CaptureType = CaptureTargetType.Window, TargetTitle = null };
        Assert.Throws<RecordingException>(() => FFmpegArgumentBuilder.Build(options, "p"));
    }

    [Fact] // 12: empty output rejected
    public void EmptyOutput_Throws()
    {
        var options = ScreenOptions() with { OutputPath = "" };
        Assert.Throws<RecordingException>(() => FFmpegArgumentBuilder.Build(options, "p"));
    }

    [Fact] // maps present
    public void Maps_VideoAndAudio()
    {
        var args = FFmpegArgumentBuilder.Build(ScreenOptions(), "p");
        AssertOrdered(args, "-map", "0:v:0");
        Assert.Contains("1:a:0", args);
        Assert.Contains("+faststart", args);
    }
}
