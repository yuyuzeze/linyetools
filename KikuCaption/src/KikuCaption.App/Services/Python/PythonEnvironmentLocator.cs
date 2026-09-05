using System.IO;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services.Python;

/// <summary>
/// The single <see cref="IPythonEnvironmentLocator"/> (R7C). Resolution order for the WORKER python:
/// config → managed LocalAppData venv → dev python/whisper_worker/.venv → other legacy venv. A
/// compatible system Python (for creating a venv) is discovered asynchronously by the installer, not
/// here. This is cheap + synchronous (file checks only) so every consumer can call it at runtime.
/// Logs only the source, never the absolute path.
/// </summary>
public sealed class PythonEnvironmentLocator : IPythonEnvironmentLocator
{
    private readonly string? _configuredWorkerPython;
    private readonly string? _devVenvPython;
    private readonly string? _legacyVenvPython;
    private readonly ILogger<PythonEnvironmentLocator> _logger;

    public PythonEnvironmentLocator(
        string? configuredWorkerPython, string? devVenvPython, string? legacyVenvPython,
        ILogger<PythonEnvironmentLocator> logger)
    {
        _configuredWorkerPython = configuredWorkerPython;
        _devVenvPython = devVenvPython;
        _legacyVenvPython = legacyVenvPython;
        _logger = logger;
    }

    public PythonEnvironmentResolution Resolve()
    {
        // 1) Config explicitly pins a venv python.
        if (IsUsableVenvPython(_configuredWorkerPython))
        {
            return Worker(_configuredWorkerPython!, PythonEnvSource.Config);
        }

        // 2) Managed venv under %LOCALAPPDATA%.
        var managed = PythonPaths.ManagedVenvDir;
        if (PythonPaths.VenvExists(managed))
        {
            return Worker(PythonPaths.VenvPython(managed), PythonEnvSource.ManagedVenv, managed);
        }

        // 3) Dev checkout venv.
        if (IsUsableVenvPython(_devVenvPython))
        {
            return Worker(_devVenvPython!, PythonEnvSource.DevVenv, Path.GetDirectoryName(Path.GetDirectoryName(_devVenvPython!)));
        }

        // 4) Some other legacy venv the user made.
        if (IsUsableVenvPython(_legacyVenvPython))
        {
            return Worker(_legacyVenvPython!, PythonEnvSource.LegacyVenv, Path.GetDirectoryName(Path.GetDirectoryName(_legacyVenvPython!)));
        }

        _logger.LogInformation("No worker Python venv resolved.");
        return new PythonEnvironmentResolution(null, PythonEnvSource.None, null, null, false);
    }

    private PythonEnvironmentResolution Worker(string python, PythonEnvSource source, string? venvDir = null)
    {
        _logger.LogInformation("Worker Python resolved from {Source}.", source);
        return new PythonEnvironmentResolution(python, source, null, venvDir, VenvHealthy: File.Exists(python));
    }

    // A usable venv python is a rooted, existing python.exe sitting under a venv (pyvenv.cfg present).
    private static bool IsUsableVenvPython(string? python)
    {
        if (string.IsNullOrWhiteSpace(python) || !Path.IsPathRooted(python) || !File.Exists(python))
        {
            return false;
        }

        var venvDir = Path.GetDirectoryName(Path.GetDirectoryName(python));
        return venvDir is not null && File.Exists(Path.Combine(venvDir, "pyvenv.cfg"));
    }
}
