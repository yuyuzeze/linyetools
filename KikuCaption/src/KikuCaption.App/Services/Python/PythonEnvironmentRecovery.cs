using System.IO;
using System.Text.Json;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services.Python;

/// <summary>
/// Startup crash-recovery for the managed venv switch (R7C.1). It runs BEFORE the environment probes and
/// the install button are available, reconciling any half-finished install (managed / backup / staging)
/// left by a forced kill or power loss. Guiding rule: always keep the last known-good environment — never
/// delete both the managed venv and its backup, and never promote a staging venv to managed unless it
/// passes the same full runtime verification the installer requires. A corrupt journal is quarantined
/// (never trusted, never deleted silently) and recovery falls back to the real on-disk directory state.
/// </summary>
public sealed class PythonEnvironmentRecovery
{
    private readonly IPythonProcessRunner _runner;
    private readonly string _pythonRoot;
    private readonly string _managed;
    private readonly PythonInstallJournal _journal;
    private readonly ILogger<PythonEnvironmentRecovery> _logger;

    public PythonEnvironmentRecovery(IPythonProcessRunner runner, ILogger<PythonEnvironmentRecovery> logger, string? pythonRoot = null)
    {
        _runner = runner;
        _pythonRoot = pythonRoot ?? PythonPaths.PythonRoot;
        _managed = Path.Combine(_pythonRoot, "venv");
        _journal = new PythonInstallJournal(_pythonRoot, logger);
        _logger = logger;
    }

    public async Task<PythonRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_pythonRoot))
        {
            return new PythonRecoveryResult(PythonRecoveryAction.None, ManagedUsable: false);
        }

        var journal = _journal.Read();
        var journalCorrupt = journal.Status == JournalReadStatus.Corrupt;
        if (journalCorrupt)
        {
            _journal.QuarantineCorrupt(); // set aside, then recover conservatively from the directory state
        }

        var backups = Leftovers(".venv-backup-");
        var stagings = Leftovers(".venv-staging-");
        var managedPresent = Structural(_managed);

        // Fast path: no interrupted transaction. Nothing to reconcile.
        if (backups.Count == 0 && stagings.Count == 0 && journal.Status == JournalReadStatus.None)
        {
            return new PythonRecoveryResult(PythonRecoveryAction.None, managedPresent);
        }

        var backup = backups.FirstOrDefault();
        var staging = stagings.FirstOrDefault();
        PythonRecoveryAction action;

        try
        {
            if (!managedPresent && backup is not null)
            {
                // Case 1: managed gone, backup present → restore the last known-good backup.
                Directory.Move(backup, _managed);
                action = PythonRecoveryAction.RestoredBackup;
            }
            else if (!managedPresent && backup is null && staging is not null)
            {
                // Case 2: managed gone, only a staging exists → promote it ONLY if fully runtime-verified.
                if (await RuntimeValidAsync(staging, cancellationToken).ConfigureAwait(false))
                {
                    Directory.Move(staging, _managed);
                    action = PythonRecoveryAction.PromotedStaging;
                }
                else
                {
                    Quarantine(staging); // keep it for diagnostics; do NOT fabricate a managed env
                    action = PythonRecoveryAction.QuarantinedStaging;
                }
            }
            else if (managedPresent && backup is not null)
            {
                // Case 3 (and 5, managed+backup+staging): validate managed; keep it only if valid.
                if (await RuntimeValidAsync(_managed, cancellationToken).ConfigureAwait(false))
                {
                    TryDelete(backup); // managed is good → the backup is no longer needed
                    action = PythonRecoveryAction.KeptManaged;
                }
                else
                {
                    Quarantine(_managed);        // never delete it outright — set aside for diagnostics
                    Directory.Move(backup, _managed); // restore the previous known-good
                    action = PythonRecoveryAction.RolledBackToBackup;
                }
            }
            else if (managedPresent && staging is not null)
            {
                // Case 4: managed + staging, no backup → never overwrite a valid managed.
                if (await RuntimeValidAsync(_managed, cancellationToken).ConfigureAwait(false))
                {
                    action = PythonRecoveryAction.KeptManaged;
                }
                else if (await RuntimeValidAsync(staging, cancellationToken).ConfigureAwait(false))
                {
                    Quarantine(_managed);
                    Directory.Move(staging, _managed);
                    action = PythonRecoveryAction.PromotedStaging;
                }
                else
                {
                    // Neither verifies — keep managed (the only candidate) rather than delete the sole venv.
                    action = PythonRecoveryAction.KeptManaged;
                }
            }
            else
            {
                // managed present, no backup/staging (or nothing present at all): honour reality.
                action = managedPresent ? PythonRecoveryAction.KeptManaged : PythonRecoveryAction.None;
            }

            CleanupLeftovers(); // remove any remaining scratch staging/backup dirs
            _journal.Clear();   // reconciled → the transaction is finished
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python environment recovery hit an error; leaving directories intact.");
            // Report the real state; do not claim success.
            return new PythonRecoveryResult(
                journalCorrupt ? PythonRecoveryAction.JournalCorrupted : PythonRecoveryAction.None,
                Structural(_managed));
        }

        if (journalCorrupt)
        {
            action = PythonRecoveryAction.JournalCorrupted; // surface that the journal was unreadable
        }

        var usable = Structural(_managed);
        _logger.LogInformation("Python environment recovery: {Action} (managed usable: {Usable}).", action, usable);
        return new PythonRecoveryResult(action, usable);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private List<string> Leftovers(string prefix) => Directory.Exists(_pythonRoot)
        ? Directory.EnumerateDirectories(_pythonRoot)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList()
        : new List<string>();

    // Structural = the directory looks like a venv (Scripts\python.exe + pyvenv.cfg present).
    private static bool Structural(string dir)
        => PythonPaths.VenvExists(dir) && File.Exists(Path.Combine(dir, "pyvenv.cfg"));

    // Full runtime verification — the SAME bar the installer uses (imports + CPU int8).
    private async Task<bool> RuntimeValidAsync(string venvDir, CancellationToken ct)
    {
        if (!Structural(venvDir)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var last = new LastLine();
            var r = await _runner.RunAsync(PythonPaths.VenvPython(venvDir), new[] { "-c", PythonVenvChecks.ImportsScript }, last, timeout.Token).ConfigureAwait(false);
            if (!r.Succeeded || last.Value is null) return false;
            using var doc = JsonDocument.Parse(last.Value);
            return doc.RootElement.GetProperty("int8").GetBoolean();
        }
        catch { return false; }
    }

    private void Quarantine(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var dest = Path.Combine(_pythonRoot, $".venv-quarantine-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            Directory.Move(dir, dest);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not quarantine a venv directory."); }
    }

    private void CleanupLeftovers()
    {
        foreach (var d in Leftovers(".venv-staging-")) TryDelete(d);
        foreach (var d in Leftovers(".venv-backup-")) TryDelete(d);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private sealed class LastLine : IProgress<string>
    {
        public string? Value { get; private set; }
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) Value = value; }
    }
}
