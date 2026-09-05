using System.Diagnostics;
using System.IO;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>Outcome of attempting a real faster-whisper/CTranslate2 load of a model directory.</summary>
public enum ModelRuntimeLoadResult
{
    /// <summary>No healthy venv/deps to run the check — the model can only be StructureVerified.</summary>
    VenvUnavailable,

    /// <summary>faster-whisper loaded the model directory successfully → RuntimeVerified.</summary>
    Loaded,

    /// <summary>A load was attempted and failed → Failed.</summary>
    Failed
}

/// <summary>Runs a real, short model-load check against a healthy Python environment (R7B.1).</summary>
public interface IModelRuntimeLoader
{
    Task<ModelRuntimeLoadResult> TryLoadAsync(string modelDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Real runtime loader (R7B.1): if the project venv has faster-whisper + ctranslate2, it runs
/// <c>WhisperModel(&lt;dir&gt;, device='cpu', compute_type='int8')</c> in a short-lived Python process
/// (no audio transcription), with a timeout and a kill-on-timeout so nothing lingers. It does NOT
/// create or install a venv (that is R7C) — when no healthy env is present it reports VenvUnavailable.
/// Only exit codes are used; no stdout content is logged.
/// </summary>
public sealed class PythonModelRuntimeLoader : IModelRuntimeLoader
{
    private readonly WhisperWorkerOptions _worker;
    private readonly ILogger<PythonModelRuntimeLoader> _logger;
    private readonly TimeSpan _timeout;

    public PythonModelRuntimeLoader(WhisperWorkerOptions worker, ILogger<PythonModelRuntimeLoader> logger, TimeSpan? timeout = null)
    {
        _worker = worker;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public async Task<ModelRuntimeLoadResult> TryLoadAsync(string modelDirectory, CancellationToken cancellationToken)
    {
        var python = _worker.PythonExecutable;
        // A managed runtime check needs a concrete, existing interpreter (the project venv). A bare
        // "python" on PATH is not treated as a healthy managed env here.
        if (string.IsNullOrWhiteSpace(python) || !Path.IsPathRooted(python) || !File.Exists(python))
        {
            return ModelRuntimeLoadResult.VenvUnavailable;
        }

        // 1) Are the deps importable? If not, there is no healthy env — do not call it a failure.
        var deps = await RunPythonAsync(python, "import faster_whisper, ctranslate2", null, cancellationToken).ConfigureAwait(false);
        if (deps != 0)
        {
            _logger.LogInformation("Runtime verification skipped: faster-whisper/ctranslate2 not importable.");
            return ModelRuntimeLoadResult.VenvUnavailable;
        }

        // 2) Actually load the model directory (no transcription).
        const string load = "import sys; from faster_whisper import WhisperModel; WhisperModel(sys.argv[1], device='cpu', compute_type='int8')";
        var code = await RunPythonAsync(python, load, modelDirectory, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Runtime model load exit code: {Code}.", code);
        return code == 0 ? ModelRuntimeLoadResult.Loaded : ModelRuntimeLoadResult.Failed;
    }

    private async Task<int> RunPythonAsync(string python, string script, string? arg, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        if (arg is not null)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        process.Start();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning("Runtime model load timed out and was killed.");
            return -1; // timeout → treated as a failed load
        }
    }
}
