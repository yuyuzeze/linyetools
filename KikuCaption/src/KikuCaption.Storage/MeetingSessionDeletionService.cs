using KikuCaption.Core.Exceptions;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Storage;

/// <summary>
/// Deletes a completed meeting as one recoverable operation: quarantine its directory first,
/// delete the database graph transactionally, then remove the quarantined files. If the database
/// transaction fails, the directory is moved back to its original location.
/// </summary>
public sealed class MeetingSessionDeletionService
{
    private readonly ITranscriptStore _store;
    private readonly StorageOptions _options;
    private readonly ILogger<MeetingSessionDeletionService> _logger;

    public MeetingSessionDeletionService(ITranscriptStore store, StorageOptions options,
        ILogger<MeetingSessionDeletionService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var stored = await _store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new StorageException("session_not_found", "找不到要删除的会议。它可能已经被删除。");

        var root = _options.ResolveOutputRoot();
        var original = Path.GetFullPath(stored.Session.OutputDirectory);
        SessionPaths.EnsureWithinRoot(root, original);
        if (string.Equals(original.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException("unsafe_delete_path", "拒绝删除会议输出根目录。");
        }

        string? quarantine = null;
        if (Directory.Exists(original))
        {
            quarantine = Path.Combine(root, $".deleting-{sessionId:N}-{Guid.NewGuid():N}");
            SessionPaths.EnsureWithinRoot(root, quarantine);
            Directory.Move(original, quarantine);
        }

        try
        {
            await _store.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (quarantine is not null && Directory.Exists(quarantine) && !Directory.Exists(original))
            {
                Directory.Move(quarantine, original);
            }
            throw;
        }

        if (quarantine is not null && Directory.Exists(quarantine))
        {
            try
            {
                Directory.Delete(quarantine, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Session {SessionId} database rows were deleted, but quarantined files require manual cleanup.",
                    sessionId);
                // The user-visible session and its original folder are already gone. Keep the
                // quarantined directory out of history and retry cleanup on a later application run.
            }
        }
    }
}
