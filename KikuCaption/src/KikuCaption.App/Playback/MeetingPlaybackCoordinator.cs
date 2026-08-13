using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Storage;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.App.Playback;

public sealed class MeetingPlaybackCoordinator
{
    private readonly ITranscriptStore _store;
    private readonly StorageOptions? _storageOptions;

    public MeetingPlaybackCoordinator(ITranscriptStore store, StorageOptions? storageOptions = null)
    {
        _store = store;
        _storageOptions = storageOptions;
    }

    public async Task<MeetingPlaybackSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var stored = await _store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Playback session was not found.");
        var recording = stored.Session.RecordingPath;
        if (string.IsNullOrWhiteSpace(recording))
            throw new InvalidOperationException("This session has no recording.");

        var path = ResolveRecordingPath(stored.Session, recording);

        var segments = await _store.GetSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var captions = segments.Where(x => x.Segment.Status != TranscriptStatus.Partial)
            .OrderBy(x => x.SequenceNumber).Select(x => x.Segment).ToArray();
        return new MeetingPlaybackSession(stored.Session, path, captions);
    }

    private string ResolveRecordingPath(KikuCaption.Core.Models.MeetingSession session, string recording)
    {
        var fileName = Path.GetFileName(recording);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "meeting.mp4";

        var candidates = new List<string>();
        AddCandidate(Path.IsPathRooted(recording)
            ? recording
            : Path.Combine(session.OutputDirectory, recording));
        AddCandidate(Path.Combine(session.OutputDirectory, fileName));
        AddCandidate(Path.Combine(session.OutputDirectory, "meeting.mp4"));

        // OutputDirectory and RecordingPath were historically stored as absolute paths.
        // When the app/repository is moved to another Windows account, rebuild the
        // session directory from its stable session id under the current output root.
        if (_storageOptions is not null)
        {
            var currentSessionDirectory = SessionPaths.BuildSessionDirectory(
                _storageOptions.ResolveOutputRoot(), session);
            AddCandidate(Path.Combine(currentSessionDirectory, fileName));
            AddCandidate(Path.Combine(currentSessionDirectory, "meeting.mp4"));
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;

        throw new FileNotFoundException(
            "The meeting recording could not be found.", candidates.FirstOrDefault());

        void AddCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var fullPath = Path.GetFullPath(candidate);
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fullPath);
        }
    }
}
