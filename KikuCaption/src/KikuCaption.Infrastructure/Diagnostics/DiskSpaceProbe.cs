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
                DetectedVersion = string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB 可用", freeGb),
                Detail = string.Format(
                    CultureInfo.InvariantCulture,
                    "驱动器 {0} 当前可用 {1:0.0} GB（最低要求 {2:0.0} GB）。",
                    drive.Name, freeGb, minimumGb),
                Remediation = status == EnvironmentCheckStatus.Warning
                    ? "可用磁盘空间偏低，录制前请清理磁盘或更换输出目录。"
                    : null
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
                Detail = $"无法读取驱动器 {targetRoot} 的可用空间。",
                Remediation = "请确认输出目录所在驱动器存在且可访问。"
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
