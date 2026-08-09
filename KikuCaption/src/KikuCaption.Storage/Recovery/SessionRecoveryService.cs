using System.Text.Json;
using KikuCaption.Core.Interfaces;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Storage.Recovery;

public sealed record RecoveryResult(int RecoveredCount, int FailedCount, IReadOnlyList<string> Notes)
{
    public static readonly RecoveryResult None = new(0, 0, Array.Empty<string>());
}

/// <summary>
/// On startup, finds sessions that were never completed and rebuilds their files from SQLite
/// (idempotent). Corrupt export files are backed up (never silently overwritten); a corrupt
/// database surfaces an error instead of a false success; one session's failure does not block
/// the others (PROJECT.md 7).
/// </summary>
public sealed class SessionRecoveryService
{
    private readonly ITranscriptStore _store;
    private readonly ITranscriptExporter _exporter;
    private readonly ILogger<SessionRecoveryService> _logger;

    public SessionRecoveryService(ITranscriptStore store, ITranscriptExporter exporter, ILogger<SessionRecoveryService> logger)
    {
        _store = store;
        _exporter = exporter;
        _logger = logger;
    }

    public async Task<RecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        // If the database itself is broken, this throws — we must not claim success.
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var incomplete = await _store.GetIncompleteSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (incomplete.Count == 0)
        {
            return RecoveryResult.None;
        }

        int recovered = 0, failed = 0;
        var notes = new List<string>();

        foreach (var stored in incomplete)
        {
            var id = stored.Session.Id;
            var dir = stored.Session.OutputDirectory;
            try
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    RemoveStaleTempFiles(dir);
                    BackupCorruptJson(dir);
                }

                await _exporter.ExportAsync(id, dir, cancellationToken).ConfigureAwait(false);
                await _store.SetSessionStateAsync(id, SessionStates.Recovered,
                    stored.Session.EndedAt ?? DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);

                recovered++;
                notes.Add($"已恢复会话 {id:N}（{stored.SegmentCount} 段字幕）。");
                _logger.LogInformation("Recovered session {SessionId} ({Count} segments).", id, stored.SegmentCount);
            }
            catch (Exception ex)
            {
                failed++;
                notes.Add($"会话 {id:N} 恢复失败：{ex.Message}");
                _logger.LogError(ex, "Recovery failed for session {SessionId}.", id);
            }
        }

        return new RecoveryResult(recovered, failed, notes);
    }

    private void RemoveStaleTempFiles(string directory)
    {
        foreach (var temp in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            try { File.Delete(temp); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not delete stale temp {File}.", temp); }
        }
    }

    private void BackupCorruptJson(string directory)
    {
        foreach (var name in new[] { "transcript.json", "session.json" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var _ = JsonDocument.Parse(stream);
            }
            catch (JsonException)
            {
                var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                try
                {
                    File.Move(path, backup, overwrite: false);
                    _logger.LogWarning("Backed up corrupt {File} to {Backup}.", name, backup);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not back up corrupt {File}.", name);
                }
            }
            catch
            {
                // I/O issue reading the file — leave it in place.
            }
        }
    }
}
