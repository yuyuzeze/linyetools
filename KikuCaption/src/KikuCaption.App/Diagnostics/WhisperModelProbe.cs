using System.IO;
using System.Linq;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Speech.Worker;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies the Whisper model cache directory exists and is non-empty. Required for captioning
/// (red when absent). The first real load may still download; this only checks that a cache is in
/// place so the user is warned before starting a meeting.
/// </summary>
public sealed class WhisperModelProbe : IEnvironmentProbe
{
    private readonly WhisperWorkerOptions _worker;

    public WhisperModelProbe(WhisperWorkerOptions worker) => _worker = worker;

    public DependencyKind Kind => DependencyKind.WhisperModel;
    public string DisplayName => "Whisper 模型";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var dir = _worker.ModelCacheDirectory;
        bool present;
        try
        {
            present = !string.IsNullOrWhiteSpace(dir)
                && Directory.Exists(dir)
                && Directory.EnumerateFileSystemEntries(dir!).Any();
        }
        catch
        {
            present = false;
        }

        var result = present
            ? new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                ResolvedPath = dir,
                MessageCode = "EnvMsg.Model.Ok"
            }
            : new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                ResolvedPath = dir,
                MessageCode = "EnvMsg.Model.Missing",
                RemediationCode = "EnvRem.Model.Missing"
            };

        return Task.FromResult(result);
    }
}
