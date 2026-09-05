using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// The single source of truth for which Python the app uses (R7C). Worker, environment probes, model
/// runtime verifier, WAV/realtime/prewarm/correction all resolve through this ONE locator so there is
/// never a second path scheme. Resolution order: config → managed LocalAppData venv → dev
/// python/whisper_worker/.venv → other legacy venv → a compatible system Python (create-only).
/// </summary>
public interface IPythonEnvironmentLocator
{
    PythonEnvironmentResolution Resolve();
}

/// <summary>Creates / repairs the managed speech-recognition Python environment (R7C).</summary>
public interface IPythonEnvironmentInstaller
{
    /// <summary>Inspects the current environment (system Python + venv health) without changing anything.</summary>
    Task<PythonEnvironmentStatus> InspectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates (or repairs) the managed venv into a staging directory, installs the locked
    /// dependencies, verifies imports + the worker + any installed models, then atomically switches it
    /// in (backing up and restoring the old one on failure). Never leaves a half-built environment
    /// marked healthy. Only one install/repair may run at a time, and never while the worker is in use.
    /// </summary>
    Task<PythonEnvironmentInstallResult> InstallOrRepairAsync(
        IProgress<PythonInstallProgress>? progress,
        CancellationToken cancellationToken);
}
