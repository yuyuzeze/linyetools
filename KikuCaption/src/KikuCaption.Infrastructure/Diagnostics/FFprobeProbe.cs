using System.Text.RegularExpressions;
using KikuCaption.Core.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Detects ffprobe.exe, the mate of ffmpeg.exe (recording muxing needs the pair). Resolved through
/// the shared <see cref="FFmpegResolver"/> so it stays consistent with <see cref="FFmpegProbe"/>
/// and the recording module (UI-R1 §6). When ffmpeg is present but ffprobe is not, this reports a
/// warning (yellow) explaining the pair is incomplete — recording-only, never blocking captions.
/// </summary>
public sealed partial class FFprobeProbe : IEnvironmentProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly IOptions<KikuCaptionOptions> _options;

    public FFprobeProbe(IProcessRunner processRunner, IOptions<KikuCaptionOptions> options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    public DependencyKind Kind => DependencyKind.FFprobe;
    public string DisplayName => "FFprobe";

    public async Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var configured = _options.Value.Recording.FFmpegPath;
        var resolution = FFmpegResolver.Resolve(configured, AppContext.BaseDirectory);

        if (!resolution.HasFFprobe)
        {
            // Distinguish "no FFmpeg at all" from "ffmpeg found but ffprobe missing".
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = EnvironmentCheckStatus.Warning,
                DetectedVersion = null,
                MessageCode = resolution.HasFFmpeg ? "EnvMsg.FFprobe.MissingBeside" : "EnvMsg.FFprobe.MissingPair",
                RemediationCode = "EnvRem.FFprobe.Missing"
            };
        }

        var run = await _processRunner
            .RunAsync(resolution.FFprobePath!, new[] { "-version" }, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Started || run.ExitCode != 0)
        {
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = EnvironmentCheckStatus.Error,
                ResolvedPath = resolution.FFprobePath,
                MessageCode = "EnvMsg.FFprobe.NotRunnable",
                RemediationCode = "EnvRem.FFprobe.NotRunnable"
            };
        }

        var version = ExtractVersion(run.StandardOutput);
        return new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = false,
            Status = EnvironmentCheckStatus.Ok,
            DetectedVersion = version is null ? "FFprobe" : $"FFprobe {version}",
            ResolvedPath = resolution.FFprobePath,
            MessageCode = "EnvMsg.FFprobe.Ok"
        };
    }

    private static string? ExtractVersion(string text)
    {
        var match = VersionRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"ffprobe version (\S+)")]
    private static partial Regex VersionRegex();
}
