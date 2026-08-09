using System.Text.RegularExpressions;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Processes;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Detects the FFmpeg executable used for screen recording / muxing in later milestones.
/// When missing this probe reports a clear, non-crashing message (PROJECT.md 16.3).
/// </summary>
public sealed partial class FFmpegProbe : IEnvironmentProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;

    public FFmpegProbe(IProcessRunner processRunner) => _processRunner = processRunner;

    public DependencyKind Kind => DependencyKind.FFmpeg;
    public string DisplayName => "FFmpeg";

    public async Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var run = await _processRunner
            .RunAsync("ffmpeg", new[] { "-version" }, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Started)
        {
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                DetectedVersion = null,
                Detail = "未检测到 FFmpeg。后续的录屏与音视频封装功能将无法使用。",
                Remediation = "请安装 FFmpeg 并加入 PATH，或放入 tools/ffmpeg：https://www.gyan.dev/ffmpeg/builds/"
            };
        }

        var version = ExtractVersion(run.StandardOutput);
        return new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = true,
            Status = EnvironmentCheckStatus.Ok,
            DetectedVersion = version is null ? "FFmpeg" : $"FFmpeg {version}",
            Detail = "已检测到 FFmpeg 可执行文件。",
            Remediation = null
        };
    }

    private static string? ExtractVersion(string text)
    {
        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionRegex();
}
