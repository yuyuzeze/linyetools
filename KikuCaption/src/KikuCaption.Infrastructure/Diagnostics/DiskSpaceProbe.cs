using System.Globalization;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Reports free disk space on the drive that will hold meeting output, comparing it to
/// the configured minimum (PROJECT.md 11 Storage.MinimumFreeSpaceGb, 12). Low disk space
/// is a warning, not a blocking failure, so it never crashes the app.
/// </summary>
public sealed class DiskSpaceProbe : IEnvironmentProbe
{
    private readonly IOptions<KikuCaptionOptions> _options;

    public DiskSpaceProbe(IOptions<KikuCaptionOptions> options) => _options = options;

    public DependencyKind Kind => DependencyKind.DiskSpace;
    public string DisplayName => "可用磁盘空间";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var minimumGb = _options.Value.Storage.MinimumFreeSpaceGb;
        var targetRoot = ResolveTargetRoot(_options.Value.Storage.OutputDirectory);

        try
        {
            var drive = new DriveInfo(targetRoot);
            var freeBytes = drive.AvailableFreeSpace;
            var freeGb = DiskSpaceEvaluator.ToGigabytes(freeBytes);
            var status = DiskSpaceEvaluator.Evaluate(freeBytes, minimumGb);

            return Task.FromResult(new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = status,
                DetectedVersion = string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", freeGb),
                MessageCode = "EnvMsg.Disk.Info",
                MessageArguments = new[]
                {
                    drive.Name,
                    freeGb.ToString("0.0", CultureInfo.InvariantCulture),
                    minimumGb.ToString("0.0", CultureInfo.InvariantCulture)
                },
                RemediationCode = status == EnvironmentCheckStatus.Warning ? "EnvRem.Disk.Low" : null
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = EnvironmentCheckStatus.Error,
                DetectedVersion = null,
                MessageCode = "EnvMsg.Disk.Error",
                MessageArguments = new[] { targetRoot },
                RemediationCode = "EnvRem.Disk.Error"
            });
        }
    }

    private static string ResolveTargetRoot(string configuredOutputDirectory)
    {
        var full = Path.IsPathRooted(configuredOutputDirectory)
            ? configuredOutputDirectory
            : Path.Combine(AppContext.BaseDirectory, configuredOutputDirectory);

        return Path.GetPathRoot(Path.GetFullPath(full))
            ?? Path.GetPathRoot(AppContext.BaseDirectory)
            ?? AppContext.BaseDirectory;
    }
}
