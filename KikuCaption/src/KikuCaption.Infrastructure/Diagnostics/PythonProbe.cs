using System.Text.RegularExpressions;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Processes;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Detects a usable Python interpreter. Python is required by later milestones
/// (the faster-whisper worker). When it is missing this probe reports a clear,
/// non-crashing message (PROJECT.md 16.4, M0 acceptance criteria).
/// </summary>
public sealed partial class PythonProbe : IEnvironmentProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    // Candidate launchers, tried in order. "py" is the Windows Python launcher.
    private static readonly (string File, string[] Args)[] Candidates =
    {
        ("python", new[] { "--version" }),
        ("py", new[] { "-3", "--version" })
    };

    private readonly IProcessRunner _processRunner;

    public PythonProbe(IProcessRunner processRunner) => _processRunner = processRunner;

    public DependencyKind Kind => DependencyKind.Python;
    public string DisplayName => "Python";

    public async Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        foreach (var (file, args) in Candidates)
        {
            var run = await _processRunner
                .RunAsync(file, args, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!run.Started)
            {
                continue;
            }

            var version = ExtractVersion($"{run.StandardOutput}\n{run.StandardError}");
            if (version is null)
            {
                continue;
            }

            var meetsRecommended = version.Major == 3 && version.Minor >= 9;
            return new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                DetectedVersion = $"Python {version}",
                Detail = meetsRecommended
                    ? "已检测到可用的 Python 解释器（推荐 3.11）。"
                    : "已检测到 Python，但版本可能与 faster-whisper 依赖不完全兼容，推荐使用 3.11。",
                Remediation = null
            };
        }

        return new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = true,
            Status = EnvironmentCheckStatus.Missing,
            DetectedVersion = null,
            Detail = "未检测到 Python。后续的本地语音识别（faster-whisper）功能将无法使用。",
            Remediation = "请安装 Python 3.11（勾选加入 PATH）：https://www.python.org/downloads/"
        };
    }

    private static Version? ExtractVersion(string text)
    {
        var match = VersionRegex().Match(text);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version)
            ? version
            : null;
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();
}
