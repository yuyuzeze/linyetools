using KikuCaption.Recording.FFmpeg;
using Xunit;

namespace KikuCaption.Recording.Tests;

public class FFmpegLocatorTests
{
    [Fact] // 1: configured path wins
    public void Configured_PathUsed()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_ff_cfg");
        var exe = Path.Combine(dir.FullName, "ffmpeg.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var found = FFmpegLocator.LocateFFmpeg(exe, dir.FullName);
            Assert.Equal(Path.GetFullPath(exe), found);
        }
        finally { dir.Delete(true); }
    }

    [Fact] // 2: project-local tools/ffmpeg found by walking up
    public void ProjectLocal_Found()
    {
        var root = Directory.CreateTempSubdirectory("kiku_ff_local");
        var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(Path.Combine(toolDir, "ffmpeg.exe"), "stub");
        var start = Path.Combine(root.FullName, "a", "b");
        Directory.CreateDirectory(start);
        try
        {
            var found = FFmpegLocator.LocateFFmpeg(null, start);
            Assert.Equal(Path.Combine(toolDir, "ffmpeg.exe"), found);
        }
        finally { root.Delete(true); }
    }

    [Fact] // 4: missing → null
    public void Missing_ReturnsNull()
    {
        // A fresh temp dir with no tools/ffmpeg above it. (ffmpeg is not on this machine's PATH.)
        var dir = Directory.CreateTempSubdirectory("kiku_ff_none");
        try
        {
            var found = FFmpegLocator.LocateFFmpeg(null, dir.FullName);
            Assert.Null(found);
        }
        finally { dir.Delete(true); }
    }

    [Fact] // ffprobe discovered next to ffmpeg
    public void FFprobe_FoundBesideFFmpeg()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_ff_probe");
        File.WriteAllText(Path.Combine(dir.FullName, "ffmpeg.exe"), "stub");
        File.WriteAllText(Path.Combine(dir.FullName, "ffprobe.exe"), "stub");
        try
        {
            var probe = FFmpegLocator.LocateFFprobe(Path.Combine(dir.FullName, "ffmpeg.exe"));
            Assert.Equal(Path.Combine(dir.FullName, "ffprobe.exe"), probe);
        }
        finally { dir.Delete(true); }
    }
}
