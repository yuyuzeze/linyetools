using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services.Python;

/// <summary>
/// R7C create/install/repair of the managed speech-recognition venv. Builds into a STAGING venv,
/// installs the locked deps, verifies imports + worker + installed models, then atomically switches it
/// in (backup → rename → restore-on-failure). It never marks a half-built environment healthy, runs
/// only one task at a time, never switches while the worker is in use, uses argument lists (no shell
/// concatenation), scrubs URLs from diagnostics, and leaves legacy/dev venvs untouched.
/// </summary>
public sealed partial class PythonEnvironmentInstaller : IPythonEnvironmentInstaller
{
    private readonly ISystemPythonDetector _detector;
    private readonly IPythonProcessRunner _runner;
    private readonly IPythonEnvironmentLocator _locator;
    private readonly IModelUsageRegistry _usage;
    private readonly IWhisperModelLocator _modelLocator;
    private readonly ModelCatalog _catalog;
    private readonly string? _configuredPython;
    private readonly string _workerScript;
    private readonly string _requirementsLock;
    private readonly string _pythonRoot;
    private readonly string _managedVenvDir;
    private readonly ILogger<PythonEnvironmentInstaller> _logger;
    private readonly SemaphoreSlim _single = new(1, 1);
    private readonly IInstallLock _installLock;
    private readonly PythonInstallJournal _journal;

    public PythonEnvironmentInstaller(
        ISystemPythonDetector detector, IPythonProcessRunner runner, IPythonEnvironmentLocator locator,
        IModelUsageRegistry usage, IWhisperModelLocator modelLocator, ModelCatalog catalog,
        string? configuredPython, string workerScript, string requirementsLock,
        ILogger<PythonEnvironmentInstaller> logger, string? pythonRoot = null, IInstallLock? installLock = null)
    {
        _detector = detector;
        _runner = runner;
        _locator = locator;
        _usage = usage;
        _modelLocator = modelLocator;
        _catalog = catalog;
        _configuredPython = configuredPython;
        _workerScript = workerScript;
        _requirementsLock = requirementsLock;
        _pythonRoot = pythonRoot ?? PythonPaths.PythonRoot;
        _managedVenvDir = Path.Combine(_pythonRoot, "venv");
        _logger = logger;
        _installLock = installLock ?? new CrossProcessInstallLock();
        _journal = new PythonInstallJournal(_pythonRoot, logger);
    }

    public async Task<PythonEnvironmentStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var resolution = _locator.Resolve();
        var candidates = await _detector.DetectAsync(_configuredPython, cancellationToken).ConfigureAwait(false);
        var best = SystemPythonDetector.Best(candidates);

        // Is the resolved worker venv healthy (deps importable)?
        string? venvPy = resolution.WorkerPython;
        ImportInfo? imports = venvPy is not null ? await TryImportsAsync(venvPy, cancellationToken).ConfigureAwait(false) : null;

        PythonEnvState state;
        if (imports is not null)
        {
            state = PythonEnvState.VenvHealthy;
        }
        else if (PythonPaths.VenvExists(_managedVenvDir) || venvPy is not null)
        {
            state = best is not null ? PythonEnvState.VenvBroken : PythonEnvState.VenvBroken;
        }
        else
        {
            state = best is not null ? PythonEnvState.NoVenv : PythonEnvState.NoSystemPython;
        }

        return new PythonEnvironmentStatus(state, best?.Info?.Version, imports?.Python,
            imports?.FasterWhisper, imports?.CTranslate2);
    }

    public async Task<PythonEnvironmentInstallResult> InstallOrRepairAsync(
        IProgress<PythonInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_single.Wait(0))
        {
            return Fail(PythonInstallPhase.Failed, "busy"); // R7C: only one install/repair per process
        }

        IDisposable? crossProcess = null;
        var staging = Path.Combine(_pythonRoot, ".venv-staging-" + Guid.NewGuid().ToString("N"));
        string? backup = null;
        try
        {
            // R7C: never install/switch while the worker is using a model (a captioned meeting / WAV / prewarm / correction).
            if (_usage.IsInUse(ModelCatalog.Small.ComponentId) || _usage.IsInUse(ModelCatalog.Medium.ComponentId))
            {
                return Fail(PythonInstallPhase.Failed, "in-use");
            }

            // R7C.1: cross-process lock — a second KikuCaption instance can't install/switch at the same time.
            crossProcess = await _installLock.TryAcquireAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (crossProcess is null)
            {
                return Fail(PythonInstallPhase.Failed, "locked");
            }

            Report(progress, PythonInstallPhase.Inspecting);
            var candidates = await _detector.DetectAsync(_configuredPython, cancellationToken).ConfigureAwait(false);
            var system = SystemPythonDetector.Best(candidates);
            if (system is null)
            {
                return Fail(PythonInstallPhase.Failed, "no-python");
            }

            if (!RequirementsAreFullyPinned(out var reqError))
            {
                return Fail(PythonInstallPhase.Failed, reqError);
            }

            Directory.CreateDirectory(_pythonRoot);
            var managed = _managedVenvDir;

            // R7C.1: open the durable transaction journal so a crash mid-switch is recoverable at startup.
            _journal.Begin(Guid.NewGuid().ToString("N"), managed, staging);

            // 1) Create the staging venv from the system Python.
            Report(progress, PythonInstallPhase.CreatingVenv);
            if (!(await _runner.RunAsync(system.Executable, new[] { "-m", "venv", staging }, null, cancellationToken)).Succeeded)
            {
                return Fail(PythonInstallPhase.Failed, "venv-create");
            }

            var stagingPy = PythonPaths.VenvPython(staging);

            // 2) Install the locked deps.
            Report(progress, PythonInstallPhase.InstallingPackages);
            var pip = await _runner.RunAsync(stagingPy,
                new[] { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "-r", _requirementsLock },
                new Progress<string>(l => Report(progress, PythonInstallPhase.InstallingPackages, l)),
                cancellationToken).ConfigureAwait(false);
            if (!pip.Succeeded)
            {
                return Fail(PythonInstallPhase.Failed, "pip");
            }

            // 3) Verify imports (+ CTranslate2 CPU int8).
            Report(progress, PythonInstallPhase.VerifyingImports);
            var imports = await TryImportsAsync(stagingPy, cancellationToken).ConfigureAwait(false);
            if (imports is null || !imports.Int8)
            {
                return Fail(PythonInstallPhase.Failed, "imports");
            }

            // 4) Verify the worker modules import under the new venv.
            Report(progress, PythonInstallPhase.VerifyingWorker);
            if (!await WorkerSelfCheckAsync(stagingPy, cancellationToken).ConfigureAwait(false))
            {
                return Fail(PythonInstallPhase.Failed, "worker");
            }

            // 5) Light-load any installed managed models with the STAGING venv (best-effort → RuntimeVerified later).
            Report(progress, PythonInstallPhase.VerifyingModels);
            var reverify = await LightLoadModelsAsync(stagingPy, cancellationToken).ConfigureAwait(false);
            _journal.Advance(PythonInstallTxPhase.StagingVerified);

            // 6) Commit. From here there is NO cancellation between the two renames (avoid a no-official-venv window).
            Report(progress, PythonInstallPhase.Committing);
            if (Directory.Exists(managed))
            {
                backup = Path.Combine(_pythonRoot, ".venv-backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(managed, backup);
                _journal.Advance(PythonInstallTxPhase.OldEnvironmentBackedUp, backup);
            }

            try
            {
                Directory.Move(staging, managed);
            }
            catch (Exception ex)
            {
                TryDelete(managed);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, managed);
                _journal.Clear();
                _logger.LogWarning(ex, "Python env commit failed; restored previous venv.");
                return Fail(PythonInstallPhase.Failed, "commit"); // old env restored → receipts untouched
            }

            _journal.Advance(PythonInstallTxPhase.NewEnvironmentActivated);

            // 7) Re-verify the NOW-active managed venv (activation could differ from staging). This runs to
            // completion (no cancellation) so we either trust the new env or deterministically roll back.
            var postVerify = await TryImportsAsync(PythonPaths.VenvPython(managed), CancellationToken.None).ConfigureAwait(false);
            if (postVerify is null || !postVerify.Int8)
            {
                TryDelete(managed);
                if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, managed);
                _journal.Clear();
                _logger.LogWarning("Post-activation verification failed; rolled back to the previous venv.");
                return Fail(PythonInstallPhase.Failed, "post-verify"); // do NOT upgrade/clear model receipts
            }

            _journal.Advance(PythonInstallTxPhase.ActiveEnvironmentVerified);

            // 8) ONLY NOW — staging verified → activated → managed re-verified → is the environment trusted.
            //    Upgrade the receipts of models that loaded successfully.
            foreach (var dir in reverify)
            {
                UpgradeReceiptToRuntimeVerified(dir);
            }

            if (backup is not null) TryDelete(backup);
            _journal.Advance(PythonInstallTxPhase.Completed);

            Report(progress, PythonInstallPhase.Completed);
            _logger.LogInformation("Python env installed ({Py}, fw {Fw}, ct2 {Ct2}).",
                postVerify.Python, postVerify.FasterWhisper, postVerify.CTranslate2);
            return new PythonEnvironmentInstallResult(true, PythonInstallPhase.Completed, null,
                postVerify.Python, postVerify.FasterWhisper, postVerify.CTranslate2);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Python env install cancelled.");
            return Fail(PythonInstallPhase.Cancelled, "cancelled");
        }
        finally
        {
            TryDelete(staging); // staging is only ever scratch
            _journal.Clear();   // any return leaves managed consistent (untouched / restored / committed)
            crossProcess?.Dispose();
            _single.Release();
        }
    }

    // ---- verification helpers ------------------------------------------------------------------

    private sealed record ImportInfo(string Python, string FasterWhisper, string CTranslate2, bool Int8);

    private async Task<ImportInfo?> TryImportsAsync(string python, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var last = new LastLine();
            var r = await _runner.RunAsync(python, new[] { "-c", PythonVenvChecks.ImportsScript }, last, timeout.Token).ConfigureAwait(false);
            if (!r.Succeeded || last.Value is null) return null;
            using var doc = JsonDocument.Parse(last.Value);
            var e = doc.RootElement;
            return new ImportInfo(e.GetProperty("py").GetString()!, e.GetProperty("fw").GetString()!,
                e.GetProperty("ct2").GetString()!, e.GetProperty("int8").GetBoolean());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch { return null; }
    }

    private async Task<bool> WorkerSelfCheckAsync(string python, CancellationToken ct)
    {
        var workerDir = Path.GetDirectoryName(_workerScript);
        if (string.IsNullOrWhiteSpace(workerDir) || !File.Exists(_workerScript)) return true; // no worker to check
        var script = $"import sys;sys.path.insert(0, r'{workerDir}');import protocol, streaming, recognizer, main";
        var r = await _runner.RunAsync(python, new[] { "-c", script }, null, ct).ConfigureAwait(false);
        return r.Succeeded;
    }

    // Loads each installed managed model with the staging venv; returns the dirs that loaded OK.
    private async Task<List<string>> LightLoadModelsAsync(string python, CancellationToken ct)
    {
        var loaded = new List<string>();
        foreach (var name in new[] { "small", "medium" })
        {
            var res = _modelLocator.Resolve(name, WhisperModelPurpose.PostMeetingCorrection);
            if (!res.IsManagedInstall) continue;
            var script = "import sys;from faster_whisper import WhisperModel;WhisperModel(sys.argv[1],device='cpu',compute_type='int8')";
            var r = await _runner.RunAsync(python, new[] { "-c", script, res.ResolvedPath }, null, ct).ConfigureAwait(false);
            if (r.Succeeded) loaded.Add(res.ResolvedPath);
        }

        return loaded;
    }

    private void UpgradeReceiptToRuntimeVerified(string modelDir)
    {
        try
        {
            var existing = InstallReceiptStore.TryRead(modelDir);
            if (existing is null || existing.VerificationLevel == ModelVerificationLevel.RuntimeVerified) return;
            InstallReceiptStore.Write(modelDir, existing with { VerificationLevel = ModelVerificationLevel.RuntimeVerified });
            _logger.LogInformation("Model {Id} upgraded to RuntimeVerified after venv install.", existing.ComponentId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not upgrade a model receipt.");
        }
    }

    private bool RequirementsAreFullyPinned(out string errorCode)
    {
        errorCode = "requirements";
        if (!File.Exists(_requirementsLock)) return false;
        foreach (var raw in File.ReadAllLines(_requirementsLock))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!PinnedLine().IsMatch(line)) return false; // every direct dep must be name==version
        }

        errorCode = string.Empty;
        return true;
    }

    private static void Report(IProgress<PythonInstallProgress>? p, PythonInstallPhase phase, string? detail = null)
        => p?.Report(new PythonInstallProgress(phase, detail, null));

    private static PythonEnvironmentInstallResult Fail(PythonInstallPhase phase, string code)
        => new(false, phase, code, null, null, null);

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private sealed class LastLine : IProgress<string>
    {
        public string? Value { get; private set; }
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) Value = value; }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+==[^\s#]+")]
    private static partial Regex PinnedLine();
}
