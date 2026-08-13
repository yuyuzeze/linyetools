using System.Diagnostics;
using System.IO;
using KikuCaption.Core.Enums;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Summarization;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>The session a summary targets (a stopped current session or a loaded history session).</summary>
public sealed record SummarySessionContext(
    Guid SessionId,
    string SessionDirectory,
    string SourceLanguage,
    DateTimeOffset SessionDate,
    SessionState State,
    int FinalCount);

/// <summary>
/// App-side bridge for meeting summaries (UI-R5C): gating, building the immutable request snapshot
/// from SQLite final captions, and opening the result file/folder safely. It never re-implements the
/// AI pipeline (that is <see cref="IMeetingSummaryService"/>); it only prepares the request and
/// surfaces the output. Testable without WPF.
/// </summary>
public sealed class MeetingSummaryCoordinator
{
    private readonly ITranscriptStore _store;
    private readonly IMeetingSummaryService _service;
    private readonly IMeetingSummaryExporter _exporter;
    private readonly ILogger<MeetingSummaryCoordinator> _logger;

    public MeetingSummaryCoordinator(
        ITranscriptStore store,
        IMeetingSummaryService service,
        IMeetingSummaryExporter exporter,
        ILogger<MeetingSummaryCoordinator> logger)
    {
        _store = store;
        _service = service;
        _exporter = exporter;
        _logger = logger;
    }

    public string DefaultFileName => _exporter.DefaultFileName;

    /// <summary>A collision-safe timestamped file name in the session directory (UI-R5C overwrite/version).</summary>
    public string UniqueVersionedFileName(string sessionDirectory, DateTimeOffset ts)
        => _exporter.UniqueVersionedFileName(sessionDirectory, ts);

    /// <summary>Reads the session's final captions and returns the count + validated time range (for the dialog).</summary>
    public async Task<(int Count, MeetingTimeRange Range)> LoadStatsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var stored = await _store.GetSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var segs = stored
            .Where(s => IsConfirmedOriginal(s.Segment.Status))
            .OrderBy(s => s.SequenceNumber)
            .Select(s => new MeetingSummarySegment(s.SequenceNumber, s.Segment.StartTime, s.Segment.EndTime, s.Segment.Text))
            .ToList();
        return (segs.Count, MeetingSummaryTimeRange.Compute(segs));
    }

    /// <summary>
    /// A summary may be generated only for a session that is NOT starting/running/stopping and that
    /// has at least one final caption. Mirrors the SessionStateMachine's "busy" set.
    /// </summary>
    public static bool CanGenerate(SessionState state, int finalCount)
    {
        bool busy = state is SessionState.Preflight or SessionState.Starting
            or SessionState.Running or SessionState.Stopping;
        return !busy && finalCount > 0;
    }

    /// <summary>Reads the session's final captions from SQLite and snapshots an immutable request.</summary>
    public async Task<MeetingSummaryRequest> BuildRequestAsync(
        SummarySessionContext context,
        MeetingType meetingType,
        string outputLanguage,
        string model,
        CancellationToken cancellationToken)
    {
        if (!CanGenerate(context.State, context.FinalCount))
        {
            throw new MeetingSummaryException(TranslationErrorCode.BadRequest, "当前会话状态不允许生成会议要点。");
        }

        var stored = await _store.GetSegmentsAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
        var segments = stored
            // Translated/TranslationFailed are terminal states of an original final caption. Include
            // their original Text, never their Translation property.
            .Where(s => IsConfirmedOriginal(s.Segment.Status))
            .OrderBy(s => s.SequenceNumber)
            .Select(s => new MeetingSummarySegment(s.SequenceNumber, s.Segment.StartTime, s.Segment.EndTime, s.Segment.Text))
            .ToList();

        if (segments.Count == 0)
        {
            throw new MeetingSummaryException(TranslationErrorCode.BadRequest, "该会话没有可用于生成要点的原文字幕。");
        }

        return new MeetingSummaryRequest
        {
            SessionId = context.SessionId,
            SessionDirectory = context.SessionDirectory,
            MeetingType = meetingType,
            OutputLanguage = outputLanguage,
            Model = model,
            PromptVersion = MeetingSummaryPrompt.Version,
            SourceLanguage = context.SourceLanguage,
            SessionDate = context.SessionDate,
            Segments = segments
        };
    }

    public Task<MeetingSummaryResult> GenerateAsync(
        MeetingSummaryRequest request,
        string fileName,
        IProgress<MeetingSummaryProgress>? progress,
        CancellationToken cancellationToken)
        => _service.GenerateAsync(request, fileName, progress, cancellationToken);

    /// <summary>True when the default summary file exists in the session directory.</summary>
    public bool SummaryExists(string sessionDirectory)
        => File.Exists(SafePath(sessionDirectory, _exporter.DefaultFileName));

    /// <summary>Opens the summary with the default app (structured args — never a shell string).</summary>
    public bool OpenSummary(string sessionDirectory)
    {
        var path = SafePath(sessionDirectory, _exporter.DefaultFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return true;
    }

    /// <summary>Reveals the summary in Explorer, or opens the session directory if it is missing.</summary>
    public void ShowInFolder(string sessionDirectory)
    {
        var path = SafePath(sessionDirectory, _exporter.DefaultFileName);
        var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
        if (File.Exists(path))
        {
            psi.ArgumentList.Add("/select,");
            psi.ArgumentList.Add(path);
        }
        else
        {
            psi.ArgumentList.Add(Path.GetFullPath(sessionDirectory));
        }

        try { Process.Start(psi); }
        catch (Exception ex) { _logger.LogWarning(ex, "Show-in-folder failed."); }
    }

    private static bool IsConfirmedOriginal(TranscriptStatus status)
        => status is TranscriptStatus.Final or TranscriptStatus.Translated or TranscriptStatus.TranslationFailed;

    // Confine to the session directory (reuses the exporter's traversal guard).
    private static string SafePath(string sessionDirectory, string fileName)
        => MarkdownMeetingSummaryExporter.ResolveSafePath(sessionDirectory, fileName);
}
