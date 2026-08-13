using KikuCaption.Core.Enums;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Summarization;

public interface IMeetingSummaryService
{
    Task<MeetingSummaryResult> GenerateAsync(
        MeetingSummaryRequest request,
        string fileName,
        IProgress<MeetingSummaryProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generates one summary from one immutable snapshot. All original final captions are sent once,
/// in sequence order; translations, partial captions and media are not part of the request model.
/// </summary>
public sealed class MeetingSummaryService : IMeetingSummaryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IMeetingSummaryClient _client;
    private readonly IMeetingSummaryExporter _exporter;
    private readonly MeetingSummaryOptions _options;
    private readonly ILogger<MeetingSummaryService> _logger;

    public MeetingSummaryService(
        IMeetingSummaryChunker chunker,
        IMeetingSummaryClient client,
        IMeetingSummaryExporter exporter,
        MeetingSummaryOptions options,
        ILogger<MeetingSummaryService> logger)
    {
        _ = chunker; // Kept for DI compatibility. The summary path deliberately does not chunk.
        _client = client;
        _exporter = exporter;
        _options = options;
        _logger = logger;
    }

    public async Task<MeetingSummaryResult> GenerateAsync(
        MeetingSummaryRequest request,
        string fileName,
        IProgress<MeetingSummaryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Segments.Count == 0)
        {
            throw new MeetingSummaryException(TranslationErrorCode.BadRequest, "No final captions are available for this meeting.");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation(
                "Summary start: session={Session} segments={Segments} type={Type} lang={Lang} model={Model} promptV={V}.",
                request.SessionId, request.SegmentCount, request.MeetingType, request.OutputLanguage,
                request.Model, request.PromptVersion);

            Report(progress, MeetingSummaryPhase.Preparing, 0, 0);
            var ordered = request.Segments.OrderBy(s => s.Sequence).ToArray();
            var single = new MeetingSummaryChunk(
                0,
                ordered[0].Start,
                ordered[^1].End,
                ordered,
                OversizedSingleSegment: ordered.Sum(s => s.Text.Length) > _options.EffectiveChunkBudget);

            _logger.LogInformation(
                "Summary single request: session={Session} segments={Segments} chars={Chars}.",
                request.SessionId, ordered.Length, single.CharCount);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, MeetingSummaryPhase.Mapping, 1, 1);
            var sections = await _client.MapAsync(request, single, cancellationToken).ConfigureAwait(false);

            var document = BuildDocument(request, sections);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, MeetingSummaryPhase.Writing, 0, 0);
            var path = await _exporter.WriteAsync(
                document, request.SessionDirectory, fileName, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Summary done: session={Session} requests=1 path-written=true.", request.SessionId);
            Report(progress, MeetingSummaryPhase.Completed, 0, 0);
            return new MeetingSummaryResult(document, path);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static MeetingSummaryDocument BuildDocument(
        MeetingSummaryRequest request,
        MeetingSummarySections sections)
    {
        var range = MeetingSummaryTimeRange.Compute(request.Segments);
        return new MeetingSummaryDocument
        {
            SessionId = request.SessionId,
            MeetingType = request.MeetingType,
            OutputLanguage = request.OutputLanguage,
            Model = request.Model,
            PromptVersion = request.PromptVersion,
            GeneratedAt = DateTimeOffset.Now,
            SessionDate = request.SessionDate,
            SegmentCount = request.SegmentCount,
            Start = range.Start,
            End = range.End,
            Sections = sections
        };
    }

    private static void Report(
        IProgress<MeetingSummaryProgress>? progress,
        MeetingSummaryPhase phase,
        int current,
        int total)
        => progress?.Report(new MeetingSummaryProgress(phase, current, total));
}
