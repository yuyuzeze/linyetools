namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Exports a session's persisted final segments to user-readable files
/// (transcript.json / transcript.txt / transcript.srt / session.json) — PROJECT.md 8.4, 12.
/// Writes are atomic (temp file + replace) so a partial write never looks valid.
/// </summary>
public interface ITranscriptExporter
{
    Task ExportAsync(Guid sessionId, string outputDirectory, CancellationToken cancellationToken);
}
