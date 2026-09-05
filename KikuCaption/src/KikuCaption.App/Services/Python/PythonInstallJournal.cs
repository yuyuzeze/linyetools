using System.IO;
using System.Text.Json;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services.Python;

/// <summary>Outcome of reading the install journal.</summary>
public enum JournalReadStatus { None, Valid, Corrupt }

/// <summary>A journal read: <see cref="Entry"/> is non-null only when <see cref="Status"/> is Valid.</summary>
public sealed record JournalRead(JournalReadStatus Status, PythonInstallJournalEntry? Entry);

/// <summary>
/// The durable install-transaction journal (R7C.1):
/// <c>%LOCALAPPDATA%\KikuCaption\python\install-state.json</c>. Written ATOMICALLY (temp file + replace)
/// at each transaction phase so a crash / power loss between the two directory renames is recoverable at
/// next startup. It records ONLY structural facts — paths, phase, operation id, timestamp — never
/// secrets, proxy, package-index URLs, or pip output. A clean, finished environment has NO journal file.
/// </summary>
public sealed class PythonInstallJournal
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;
    private PythonInstallJournalEntry? _current;

    public PythonInstallJournal(string pythonRoot, ILogger logger)
    {
        _path = Path.Combine(pythonRoot, "install-state.json");
        _logger = logger;
    }

    /// <summary>The journal file path (also used to derive the quarantine name).</summary>
    public string FilePath => _path;

    /// <summary>Start a new transaction at <see cref="PythonInstallTxPhase.Preparing"/>.</summary>
    public void Begin(string operationId, string managedPath, string stagingPath)
    {
        _current = new PythonInstallJournalEntry(
            CurrentSchemaVersion, operationId, PythonInstallTxPhase.Preparing,
            managedPath, stagingPath, BackupPath: null, StartedAt: DateTime.UtcNow.ToString("O"));
        WriteAtomic(_current);
    }

    /// <summary>Advance to a later phase (optionally recording the backup path once it exists).</summary>
    public void Advance(PythonInstallTxPhase phase, string? backupPath = null)
    {
        if (_current is null) return;
        _current = _current with { Phase = phase, BackupPath = backupPath ?? _current.BackupPath };
        WriteAtomic(_current);
    }

    /// <summary>Transaction finished (committed OR fully rolled back) → remove the journal file.</summary>
    public void Clear()
    {
        _current = null;
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete the install journal."); }
    }

    /// <summary>Read + validate the journal. A schema/shape mismatch or parse error is reported as Corrupt.</summary>
    public JournalRead Read()
    {
        if (!File.Exists(_path)) return new JournalRead(JournalReadStatus.None, null);
        try
        {
            var entry = JsonSerializer.Deserialize<PythonInstallJournalEntry>(File.ReadAllText(_path), JsonOptions);
            if (entry is null || entry.SchemaVersion != CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(entry.ManagedPath) || string.IsNullOrWhiteSpace(entry.StagingPath))
            {
                return new JournalRead(JournalReadStatus.Corrupt, null);
            }

            return new JournalRead(JournalReadStatus.Valid, entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "install-state.json is unreadable; treating it as corrupt.");
            return new JournalRead(JournalReadStatus.Corrupt, null);
        }
    }

    /// <summary>Move a corrupt journal aside as <c>install-state.json.corrupt-&lt;timestamp&gt;.bak</c> (never silently deleted).</summary>
    public void QuarantineCorrupt()
    {
        try
        {
            if (!File.Exists(_path)) return;
            File.Move(_path, _path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak", overwrite: true);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not quarantine the corrupt install journal."); }
    }

    private void WriteAtomic(PythonInstallJournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entry, JsonOptions));
        File.Move(tmp, _path, overwrite: true); // MoveFileEx REPLACE_EXISTING → atomic on the same volume
    }
}
