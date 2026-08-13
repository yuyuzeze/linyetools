using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.App.Playback;

public sealed class MeetingPlaybackCoordinator
{
    private readonly ITranscriptStore _store;

    public MeetingPlaybackCoordinator(ITranscriptStore store) => _store = store;

    public async Task<MeetingPlaybackSession> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var stored = await _store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Playback session was not found.");
        var recording = stored.Session.RecordingPath;
        if (string.IsNullOrWhiteSpace(recording))
            throw new InvalidOperationException("This session has no recording.");

        var path = Path.IsPathRooted(recording)
            ? Path.GetFullPath(recording)
            : Path.GetFullPath(Path.Combine(stored.Session.OutputDirectory, recording));
        if (!File.Exists(path))
            throw new FileNotFoundException("The meeting recording could not be found.", path);

        var segments = await _store.GetSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var captions = segments.Where(x => x.Segment.Status != TranscriptStatus.Partial)
            .OrderBy(x => x.SequenceNumber).Select(x => x.Segment).ToArray();
        return new MeetingPlaybackSession(stored.Session, path, captions);
    }
}
