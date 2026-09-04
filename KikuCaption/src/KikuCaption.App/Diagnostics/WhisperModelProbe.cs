using System.IO;
using System.Linq;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Speech.Worker;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies the Whisper model cache directory exists and is non-empty. Required for captioning
/// (red when absent) — UNLESS local speech recognition is off (UI-R6A), when it is a skipped/
/// informational row. The first real load may still download; this only checks that a cache is in
/// place so the user is warned before starting a caption meeting.
/// </summary>
public sealed class WhisperModelProbe : IEnvironmentProbe
{
    private readonly WhisperWorkerOptions _worker;
    private readonly UserSettingsStore _settings;

    public WhisperModelProbe(WhisperWorkerOptions worker, UserSettingsStore settings)
    {
        _worker = worker;
        _settings = settings;
    }

    public DependencyKind Kind => DependencyKind.WhisperModel;
    public string DisplayName => "Whisper 模型";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        // UI-R6A: recording-only mode does not need the model — report it as skipped, not missing.
        if (!_settings.Load().Settings.EnableSpeechRecognition)
        {
            return Task.FromResult(new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Skipped = true,
                Status = EnvironmentCheckStatus.Ok,
                MessageCode = "EnvMsg.Skipped.RecordingMode"
            });
        }

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
