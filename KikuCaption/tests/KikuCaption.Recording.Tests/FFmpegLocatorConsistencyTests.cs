using KikuCaption.Core.Diagnostics;
using KikuCaption.Recording.FFmpeg;
using Xunit;

namespace KikuCaption.Recording.Tests;

/// <summary>
/// UI-R1 §6: recording's <see cref="FFmpegLocator"/> and the shared <see cref="FFmpegResolver"/>
/// (used by the environment check + preflight) must resolve to the same paths — the whole point of
/// the fix. This locks that guarantee so the two can never drift apart again.
/// </summary>
public class FFmpegLocatorConsistencyTests
{
    [Fact]
    public void LocatorAndResolver_AgreeOnPair()
    {
        var root = Directory.CreateTempSubdirectory("kiku_consistency");
        try
        {
            var toolDir = Path.Combine(root.FullName, "tools", "ffmpeg");
            Directory.CreateDirectory(toolDir);
            File.WriteAllText(Path.Combine(toolDir, "ffmpeg.exe"), "stub");
            File.WriteAllText(Path.Combine(toolDir, "ffprobe.exe"), "stub");

            var resolution = FFmpegResolver.Resolve(null, root.FullName);
            var locatorFFmpeg = FFmpegLocator.LocateFFmpeg(null, root.FullName);
            var locatorFFprobe = FFmpegLocator.LocateFFprobe(locatorFFmpeg!);

            Assert.Equal(resolution.FFmpegPath, locatorFFmpeg);
            Assert.Equal(resolution.FFprobePath, locatorFFprobe);
        }
        finally { root.Delete(true); }
    }
}
