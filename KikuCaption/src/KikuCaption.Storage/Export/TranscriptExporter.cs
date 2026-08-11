using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.Storage.Export;

/// <summary>
/// Exports a session's persisted final segments to transcript.json / transcript.txt /
/// transcript.srt / session.json (UTF-8, deterministic order, atomic writes). Driven entirely
/// from SQLite, so it is also the recovery rebuild path.
/// </summary>
public sealed class TranscriptExporter : ITranscriptExporter
{
    /// <summary>
    /// Bumped when the on-disk file format changes. v2 (UI-R4A) adds the translation direction
    /// (translationEnabled / translationSource / translationTarget) to session.json — additive and
    /// backward-compatible (older readers ignore the new fields).
    /// </summary>
    public const int DataFormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep CJK readable
    };

    private readonly ITranscriptStore _store;
    private readonly string _appVersion;

    public TranscriptExporter(ITranscriptStore store, string appVersion)
    {
        _store = store;
        _appVersion = appVersion;
    }

    public async Task ExportAsync(Guid sessionId, string outputDirectory, CancellationToken cancellationToken)
    {
        var stored = await _store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                     ?? throw new StorageException("session_not_found", "找不到会话，无法导出。");

        // A confirmed line stays in the transcript through its whole translation lifecycle
        // (Final → Translated / TranslationFailed). Only Partial is excluded.
        var segments = (await _store.GetSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .Where(s => s.Segment.Status != TranscriptStatus.Partial && !string.IsNullOrWhiteSpace(s.Segment.Text))
            .ToList();

        var translated = segments
            .Where(s => s.Segment.Status == TranscriptStatus.Translated && !string.IsNullOrWhiteSpace(s.Segment.Translation))
            .ToList();

        Directory.CreateDirectory(outputDirectory);

        await AtomicFile.WriteAllTextAsync(Path.Combine(outputDirectory, "transcript.json"),
            BuildJson(stored, segments), cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(Path.Combine(outputDirectory, "transcript.txt"),
            BuildTxt(segments), cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(Path.Combine(outputDirectory, "transcript.srt"),
            BuildSrt(segments), cancellationToken).ConfigureAwait(false);

        // translation.srt: only successfully-translated, non-empty Chinese lines, in the SAME order
        // and with the SAME original times as the source segments; contiguous renumbering.
        if (translated.Count > 0)
        {
            await AtomicFile.WriteAllTextAsync(Path.Combine(outputDirectory, "translation.srt"),
                BuildTranslationSrt(translated), cancellationToken).ConfigureAwait(false);
        }

        await AtomicFile.WriteAllTextAsync(Path.Combine(outputDirectory, "session.json"),
            BuildSessionJson(stored, segments.Count, translated.Count), cancellationToken).ConfigureAwait(false);
    }

    private static string BuildJson(StoredSession stored, IReadOnlyList<StoredSegment> segments)
    {
        var items = segments.Select(s => new
        {
            id = s.Segment.Id.ToString("N"),
            sessionId = s.Segment.SessionId.ToString("N"),
            sequenceNumber = s.SequenceNumber,
            start = Math.Round(s.Segment.StartTime.TotalSeconds, 3),
            end = Math.Round(s.Segment.EndTime.TotalSeconds, 3),
            language = s.Segment.Language,
            text = s.Segment.Text,
            translation = s.Segment.Translation,
            status = s.Segment.Status.ToString(),
            confidence = s.Segment.Confidence,
            createdAt = s.Segment.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
        });

        return JsonSerializer.Serialize(items, JsonOptions);
    }

    private static string BuildTxt(IReadOnlyList<StoredSegment> segments)
    {
        // Fixed, documented format: "[HH:mm:ss] text" per final line.
        var builder = new StringBuilder();
        foreach (var s in segments)
        {
            builder.Append('[').Append(Clock(s.Segment.StartTime)).Append("] ").Append(s.Segment.Text.Trim()).Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildSrt(IReadOnlyList<StoredSegment> segments)
    {
        var builder = new StringBuilder();
        int index = 1;
        foreach (var s in segments)
        {
            var start = s.Segment.StartTime;
            var end = s.Segment.EndTime < start ? start : s.Segment.EndTime;

            builder.Append(index).Append("\r\n");
            builder.Append(Srt(start)).Append(" --> ").Append(Srt(end)).Append("\r\n");
            builder.Append(s.Segment.Text.Trim()).Append("\r\n\r\n");
            index++;
        }

        return builder.ToString();
    }

    private static string BuildTranslationSrt(IReadOnlyList<StoredSegment> translated)
    {
        var builder = new StringBuilder();
        int index = 1;
        foreach (var s in translated)
        {
            var start = s.Segment.StartTime;
            var end = s.Segment.EndTime < start ? start : s.Segment.EndTime;

            builder.Append(index).Append("\r\n");
            builder.Append(Srt(start)).Append(" --> ").Append(Srt(end)).Append("\r\n");
            builder.Append(s.Segment.Translation!.Trim()).Append("\r\n\r\n");
            index++;
        }

        return builder.ToString();
    }

    private string BuildSessionJson(StoredSession stored, int segmentCount, int translatedCount)
    {
        var session = stored.Session;

        // Translation direction snapshot (UI-R4A). Legacy sessions (pre-v3 DB) have null columns:
        // fall back to the historical ja→zh behaviour, inferred from whether anything was translated.
        var translationEnabled = session.TranslationEnabled ?? (translatedCount > 0);
        var translationSource = session.TranslationSource ?? (translatedCount > 0 ? "ja" : session.RecognitionLanguage);
        var translationTarget = session.TranslationTarget ?? (translatedCount > 0 ? "zh" : null);

        var dto = new
        {
            sessionId = session.Id.ToString("N"),
            startedAt = session.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            endedAt = session.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
            recognitionLanguage = session.RecognitionLanguage,
            state = stored.State,
            outputDirectory = session.OutputDirectory,
            recordingPath = session.RecordingPath,
            segmentCount,
            translatedCount,
            translationEnabled,
            translationSource,
            translationTarget,
            translationModel = session.TranslationModel,
            appVersion = _appVersion,
            dataFormatVersion = DataFormatVersion
        };

        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static string Clock(TimeSpan time)
        => $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";

    private static string Srt(TimeSpan time)
        => $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
}
