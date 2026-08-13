using KikuCaption.Core.Enums;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Summarization;

/// <summary>Generates a meeting summary from an immutable request snapshot (Map/Reduce → Markdown).</summary>
public interface IMeetingSummaryService
{
    Task<MeetingSummaryResult> GenerateAsync(
        MeetingSummaryRequest request,
        string fileName,
        IProgress<MeetingSummaryProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Meeting-summary orchestrator (UI-R5C §6/§8/§14). It has its own task lifecycle, independent of the
/// translation queue. Pipeline: validate → chunk (order-preserving) → Map each chunk → hierarchical
/// Reduce (never assuming one model context) → build the document → atomic Markdown write into the
/// request's snapshotted session directory. A global semaphore (concurrency 1) limits API pressure and
/// serializes generations; cancellation threads through every await and never writes/overwrites on
/// cancel. Only ids / counts / sizes / model / prompt version / error codes are logged.
/// </summary>
public sealed class MeetingSummaryService : IMeetingSummaryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1); // global concurrency 1

    private readonly IMeetingSummaryChunker _chunker;
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
        _chunker = chunker;
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
            throw new MeetingSummaryException(TranslationErrorCode.BadRequest, "该会话没有可用于生成要点的原文字幕。");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Summary start: session={Session} segments={Segments} type={Type} lang={Lang} model={Model} promptV={V}.",
                request.SessionId, request.SegmentCount, request.MeetingType, request.OutputLanguage, request.Model, request.PromptVersion);

            Report(progress, MeetingSummaryPhase.Preparing, 0, 0);
            var chunks = _chunker.Chunk(request.Segments, _options.EffectiveChunkBudget);
            foreach (var c in chunks.Where(c => c.OversizedSingleSegment))
            {
                _logger.LogWarning("Summary chunk {Index} holds a single over-budget segment ({Chars} chars).", c.Index, c.CharCount);
            }

            // Map: one call per chunk, in time order (sequential — keeps API pressure low, order stable).
            var parts = new List<MeetingSummarySections>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, MeetingSummaryPhase.Mapping, i + 1, chunks.Count);
                parts.Add(await _client.MapAsync(request, chunks[i], cancellationToken).ConfigureAwait(false));
            }

            // Reduce: hierarchical merge so we never depend on a single model context length.
            var merged = parts.Count == 1
                ? parts[0]
                : await ReduceHierarchicalAsync(request, parts, progress, cancellationToken).ConfigureAwait(false);

            var document = BuildDocument(request, chunks, merged);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, MeetingSummaryPhase.Writing, 0, 0);
            var path = await _exporter.WriteAsync(document, request.SessionDirectory, fileName, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Summary done: session={Session} chunks={Chunks} path-written=true.", request.SessionId, chunks.Count);
            Report(progress, MeetingSummaryPhase.Completed, 0, 0);
            return new MeetingSummaryResult(document, path);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<MeetingSummarySections> ReduceHierarchicalAsync(
        MeetingSummaryRequest request,
        IReadOnlyList<MeetingSummarySections> parts,
        IProgress<MeetingSummaryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var current = parts;
        int group = _options.EffectiveReduceGroupSize;
        int level = 0;

        while (current.Count > 1)
        {
            var next = new List<MeetingSummarySections>();
            for (int i = 0; i < current.Count; i += group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = current.Skip(i).Take(group).ToList();
                Report(progress, MeetingSummaryPhase.Reducing, next.Count + 1, 0);

                // A single leftover part needs no merge — carry it up unchanged.
                next.Add(slice.Count == 1 ? slice[0] : await _client.ReduceAsync(request, slice, cancellationToken).ConfigureAwait(false));
            }

            current = next;
            level++;
        }

        _logger.LogInformation("Summary reduce levels={Levels}.", level);
        return current[0];
    }

    private static MeetingSummaryDocument BuildDocument(MeetingSummaryRequest request, IReadOnlyList<MeetingSummaryChunk> chunks, MeetingSummarySections sections)
    {
        // Same validated min/max range the dialog shows — UI and Markdown never disagree.
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

    private static void Report(IProgress<MeetingSummaryProgress>? progress, MeetingSummaryPhase phase, int current, int total)
        => progress?.Report(new MeetingSummaryProgress(phase, current, total));
}
