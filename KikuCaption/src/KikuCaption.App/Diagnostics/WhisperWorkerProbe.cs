using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Speech.Worker;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies the local faster-whisper worker is present (Python interpreter + worker script). This
/// is required for captioning, so its absence is blocking (red). It only checks cheap facts (file
/// existence); the model load itself is validated when recognition starts.
/// </summary>
public sealed class WhisperWorkerProbe : IEnvironmentProbe
{
    private readonly WhisperWorkerOptions _worker;

    public WhisperWorkerProbe(WhisperWorkerOptions worker) => _worker = worker;

    public DependencyKind Kind => DependencyKind.WhisperWorker;
    public string DisplayName => "faster-whisper Worker";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var scriptOk = !string.IsNullOrWhiteSpace(_worker.WorkerScript) && File.Exists(_worker.WorkerScript);

        // Python may be a bare command name ("python") resolved via PATH, or an absolute venv path.
        var python = _worker.PythonExecutable;
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
