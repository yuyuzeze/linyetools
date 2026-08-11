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
                MessageCode = meetsRecommended ? "EnvMsg.Python.Ok" : "EnvMsg.Python.OkOldish"
            };
        }

        return new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = true,
            Status = EnvironmentCheckStatus.Missing,
            DetectedVersion = null,
            MessageCode = "EnvMsg.Python.Missing",
            RemediationCode = "EnvRem.Python.Missing"
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
