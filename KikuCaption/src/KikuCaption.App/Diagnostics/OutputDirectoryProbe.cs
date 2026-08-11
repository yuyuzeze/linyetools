using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Storage;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies the session output directory exists and is writable (required — captions/recordings are
/// persisted there). Performs a real create-write-delete probe on a temp file, matching the
/// preflight logic.
/// </summary>
public sealed class OutputDirectoryProbe : IEnvironmentProbe
{
    private readonly StorageOptions _storage;

    public OutputDirectoryProbe(StorageOptions storage) => _storage = storage;

    public DependencyKind Kind => DependencyKind.OutputDirectory;
    public string DisplayName => "输出目录写入权限";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var root = _storage.ResolveOutputRoot();
        var writable = DirectoryWritable(root);

        var result = writable
            ? new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                ResolvedPath = root,
                Detail = "会话输出目录可写。"
            }
            : new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                ResolvedPath = root,
                Detail = "无法在输出目录写入，字幕与录制文件将无法保存。",
                Remediation = "请在设置中改用一个可写目录，或授予该目录写入权限。"
            };

        return Task.FromResult(result);
    }

    private static bool DirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".kiku-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
