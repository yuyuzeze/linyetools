using KikuCaption.Core.Diagnostics;
using Xunit;

namespace KikuCaption.Core.Tests;

/// <summary>
/// Locator half of the UI-R1 §6 FFmpeg fix: one resolver used by the environment check, preflight
/// and recording. Uses stub files (no real ffmpeg) and an in-process PATH override — never touches
/// the system PATH permanently.
/// </summary>
public class FFmpegResolverTests
{
    private static void WritePair(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "stub");
        File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "stub");
    }

    [Fact] // 1: configured directory contains both exes
    public void ConfiguredPath_ResolvesPair()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_res_cfg");
        try
        {
            WritePair(dir.FullName);
            var ffmpeg = Path.Combine(dir.FullName, "ffmpeg.exe");

            var r = FFmpegResolver.Resolve(ffmpeg, dir.FullName);

            Assert.True(r.IsComplete);
            Assert.Equal(Path.GetFullPath(ffmpeg), r.FFmpegPath);
            Assert.Equal(Path.Combine(dir.FullName, "ffprobe.exe"), r.FFprobePath);
        }
        finally { dir.Delete(true); }
    }

    [Fact] // 2: published app's tools/ffmpeg (base directory itself)
    public void AppToolsFolder_ResolvesPair()
    {
        var root = Directory.CreateTempSubdirectory("kiku_res_app");
        try
        {
            var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
            Directory.CreateDirectory(toolDir);
            WritePair(toolDir);

            var r = FFmpegResolver.Resolve(null, root.FullName);

            Assert.True(r.IsComplete);
            Assert.Equal(Path.Combine(toolDir, "ffmpeg.exe"), r.FFmpegPath);
            Assert.Equal(Path.Combine(toolDir, "ffprobe.exe"), r.FFprobePath);
        }
        finally { root.Delete(true); }
    }

    [Fact] // 3: repository's tools/ffmpeg found by walking up from a deep dev run directory
    public void RepoToolsFolder_FoundByWalkingUp()
    {
        var root = Directory.CreateTempSubdirectory("kiku_res_repo");
        try
        {
            var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
            Directory.CreateDirectory(toolDir);
            WritePair(toolDir);
            var deep = Path.Combine(root.FullName, "src", "bin", "Debug", "net10.0-windows");
            Directory.CreateDirectory(deep);

            var r = FFmpegResolver.Resolve(null, deep);

            Assert.True(r.IsComplete);
            Assert.Equal(Path.Combine(toolDir, "ffmpeg.exe"), r.FFmpegPath);
        }
        finally { root.Delete(true); }
    }

    [Fact] // 4: system PATH lookup (in-process override, restored afterwards)
    public void SystemPath_Resolves()
    {
        var pathDir = Directory.CreateTempSubdirectory("kiku_res_path");
        var baseDir = Directory.CreateTempSubdirectory("kiku_res_pathbase");
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            WritePair(pathDir.FullName);
            Environment.SetEnvironmentVariable("PATH", pathDir.FullName);

            var r = FFmpegResolver.Resolve(null, baseDir.FullName);

            Assert.True(r.IsComplete);
            Assert.Equal(Path.Combine(pathDir.FullName, "ffmpeg.exe"), r.FFmpegPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
            pathDir.Delete(true);
            baseDir.Delete(true);
        }
    }

    [Fact] // 5: a path containing spaces is handled (no shell involved)
    public void PathWithSpaces_Resolves()
    {
        var root = Directory.CreateTempSubdirectory("kiku_res_space");
        try
        {
            var spaced = Path.Combine(root.FullName, "Program Files", "My FFmpeg Build");
            Directory.CreateDirectory(spaced);
            WritePair(spaced);
            var ffmpeg = Path.Combine(spaced, "ffmpeg.exe");

            var r = FFmpegResolver.Resolve(ffmpeg, root.FullName);

            Assert.True(r.IsComplete);
            Assert.Equal(Path.GetFullPath(ffmpeg), r.FFmpegPath);
            Assert.Contains(" ", r.FFprobePath);
        }
        finally { root.Delete(true); }
    }

    [Fact] // 6: only ffmpeg present → ffmpeg found, ffprobe missing, pair incomplete
    public void OnlyFFmpeg_ProbeMissing()
    {
        var root = Directory.CreateTempSubdirectory("kiku_res_onlyff");
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
            Directory.CreateDirectory(toolDir);
            File.WriteAllText(Path.Combine(toolDir, "ffmpeg.exe"), "stub");
            Environment.SetEnvironmentVariable("PATH", ""); // ensure PATH cannot supply ffprobe

            var r = FFmpegResolver.Resolve(null, root.FullName);

            Assert.True(r.HasFFmpeg);
            Assert.False(r.HasFFprobe);
            Assert.False(r.IsComplete);
        }
        finally { Environment.SetEnvironmentVariable("PATH", original); root.Delete(true); }
    }

    [Fact] // 7: only ffprobe present → ffmpeg missing (pair incomplete)
    public void OnlyFFprobe_FFmpegMissing()
    {
        var root = Directory.CreateTempSubdirectory("kiku_res_onlyprobe");
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
            Directory.CreateDirectory(toolDir);
            File.WriteAllText(Path.Combine(toolDir, "ffprobe.exe"), "stub");
            Environment.SetEnvironmentVariable("PATH", "");

            var r = FFmpegResolver.Resolve(null, root.FullName);

            Assert.False(r.HasFFmpeg);
            Assert.True(r.HasFFprobe);
            Assert.False(r.IsComplete);
        }
        finally { Environment.SetEnvironmentVariable("PATH", original); root.Delete(true); }
    }

    [Fact] // 8: nothing found (and not on PATH) → both null
    public void NothingFound_ReturnsNulls()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_res_none");
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            var r = FFmpegResolver.Resolve(null, dir.FullName);
            Assert.False(r.HasFFmpeg);
            Assert.False(r.HasFFprobe);
        }
        finally { Environment.SetEnvironmentVariable("PATH", original); dir.Delete(true); }
    }
}
