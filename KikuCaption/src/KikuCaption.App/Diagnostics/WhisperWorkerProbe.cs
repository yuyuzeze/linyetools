using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Speech.Worker;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies the local faster-whisper worker is present (Python interpreter + worker script). It is
/// required for captioning, so its absence is blocking (red) — UNLESS local speech recognition is
/// turned off (UI-R6A), in which case it is a skipped/informational row that never affects overall
/// health. Only cheap facts (file existence) are checked; the model load is validated when recognition
/// starts.
/// </summary>
public sealed class WhisperWorkerProbe : IEnvironmentProbe
{
    private readonly WhisperWorkerOptions _worker;
    private readonly UserSettingsStore _settings;
    private readonly KikuCaption.Core.Interfaces.IPythonEnvironmentLocator? _pythonLocator;

    public WhisperWorkerProbe(WhisperWorkerOptions worker, UserSettingsStore settings,
        KikuCaption.Core.Interfaces.IPythonEnvironmentLocator? pythonLocator = null)
    {
        _worker = worker;
        _settings = settings;
        _pythonLocator = pythonLocator;
    }

    public DependencyKind Kind => DependencyKind.WhisperWorker;
    public string DisplayName => "faster-whisper Worker";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        // UI-R6A: recording-only mode does not need the worker — report it as skipped, not missing.
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

        var scriptOk = !string.IsNullOrWhiteSpace(_worker.WorkerScript) && File.Exists(_worker.WorkerScript);

        // R7C: the worker Python comes from the ONE locator (managed venv → dev → …). When no venv is
        // resolved the worker is unavailable; a bare command name is only accepted as a legacy fallback.
        var python = _pythonLocator?.Resolve().WorkerPython ?? _worker.PythonExecutable;
        var pythonOk = !string.IsNullOrWhiteSpace(python)
            && (File.Exists(python) || !Path.IsPathRooted(python));

        DependencyCheckResult result;
        if (!scriptOk)
        {
            result = new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                MessageCode = "EnvMsg.Worker.NoScript",
                RemediationCode = "EnvRem.Worker.NoScript"
            };
        }
        else if (!pythonOk)
        {
            result = new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                ResolvedPath = _worker.WorkerScript,
                MessageCode = "EnvMsg.Worker.NoPython",
                RemediationCode = "EnvRem.Worker.NoPython"
            };
        }
        else
        {
            result = new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                DetectedVersion = Path.IsPathRooted(python!) ? "venv" : python,
                ResolvedPath = _worker.WorkerScript,
                MessageCode = "EnvMsg.Worker.Ok"
            };
        }

        return Task.FromResult(result);
    }
}
