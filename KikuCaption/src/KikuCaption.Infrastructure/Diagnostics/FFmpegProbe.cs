using System.Text.RegularExpressions;
using KikuCaption.Core.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Processes;
using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Detects the FFmpeg executable used for screen recording / muxing. It resolves the path through
/// the shared <see cref="FFmpegResolver"/> — the same resolver the recording module and preflight
/// use — so the environment check can never disagree with recording (UI-R1 §6 FFmpeg bug fix).
///
/// FFmpeg is recording-only, so its absence is non-blocking (yellow, not red): captions still run.
/// </summary>
public sealed partial class FFmpegProbe : IEnvironmentProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly IOptions<KikuCaptionOptions> _options;

    public FFmpegProbe(IProcessRunner processRunner, IOptions<KikuCaptionOptions> options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    public DependencyKind Kind => DependencyKind.FFmpeg;
    public string DisplayName => "FFmpeg";

    public async Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var configured = _options.Value.Recording.FFmpegPath;
        var resolution = FFmpegResolver.Resolve(configured, AppContext.BaseDirectory);

        if (!resolution.HasFFmpeg)
        {
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false, // recording-only → non-blocking (captions still work)
                Status = EnvironmentCheckStatus.Missing,
                DetectedVersion = null,
                MessageCode = "EnvMsg.FFmpeg.Missing",
                RemediationCode = "EnvRem.FFmpeg.Missing"
            };
        }

        // The file exists — verify it actually launches (a copied-but-broken exe must not read "OK").
        var run = await _processRunner
            .RunAsync(resolution.FFmpegPath!, new[] { "-version" }, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!run.Started || run.ExitCode != 0)
        {
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = EnvironmentCheckStatus.Error,
                DetectedVersion = null,
                ResolvedPath = resolution.FFmpegPath,
                MessageCode = "EnvMsg.FFmpeg.NotRunnable",
                RemediationCode = "EnvRem.FFmpeg.NotRunnable"
            };
        }

        var version = ExtractVersion(run.StandardOutput);
        return new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = false,
            Status = EnvironmentCheckStatus.Ok,
            DetectedVersion = version is null ? "FFmpeg" : $"FFmpeg {version}",
            ResolvedPath = resolution.FFmpegPath,
            MessageCode = "EnvMsg.FFmpeg.Ok"
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
