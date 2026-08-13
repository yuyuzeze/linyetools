using KikuCaption.Core.Enums;

namespace KikuCaption.Summarization;

/// <summary>Meeting format — drives which template/sections are emphasized. No speaker analysis either way.</summary>
public enum MeetingType
{
    /// <summary>One presenter explaining material (概要/主题/知识点/流程/结论/注意事项).</summary>
    SinglePresenter,

    /// <summary>Multiple anonymous participants discussing (概述/议题/观点/决定/待办/未决/风险).</summary>
    GroupDiscussion
}

/// <summary>Lifecycle phase reported to the UI (localized by key at the view layer).</summary>
public enum MeetingSummaryPhase
{
    Preparing,
    Mapping,
    Reducing,
    Writing,
    Completed,
    Cancelled,
    Failed
}

/// <summary>One confirmed final caption in the immutable request snapshot (original text only).</summary>
public sealed record MeetingSummarySegment(long Sequence, TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// An immutable snapshot taken when the user starts generation (UI-R5C §14). It fully determines the
/// output — session id/directory, meeting format, output language, model, prompt version, and the
/// original final captions — so a later session switch or UI change cannot redirect or corrupt the
/// result. Contains ONLY confirmed original caption text: never partials, translations, audio/video,
/// dictionaries, prompts, or keys.
/// </summary>
public sealed record MeetingSummaryRequest
{
    public required Guid SessionId { get; init; }
    public required string SessionDirectory { get; init; }
    public required MeetingType MeetingType { get; init; }

    /// <summary>Summary output language: "zh", "ja", or "en".</summary>
    public required string OutputLanguage { get; init; }

    public required string Model { get; init; }
    public required int PromptVersion { get; init; }
    public required string SourceLanguage { get; init; }
    public DateTimeOffset SessionDate { get; init; }
    public required IReadOnlyList<MeetingSummarySegment> Segments { get; init; }

    public int SegmentCount => Segments.Count;

    public TimeSpan Duration => Segments.Count == 0
        ? TimeSpan.Zero
        : Segments[^1].End - Segments[0].Start;
}

/// <summary>An action/todo item. Owner/Due are "未明确"-equivalent unless the captions stated them.</summary>
public sealed record MeetingActionItem(string Task, string Owner, string Due);

/// <summary>
/// The structured content shared by chunk (Map) and final (Reduce) results. Arrays are never null
/// (missing → empty). This is what the AI returns as JSON and what the exporter renders — the model
/// never produces the final Markdown, so headings/structure stay stable and testable.
/// </summary>
public sealed record MeetingSummarySections
{
    public string Overview { get; init; } = string.Empty;
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KeyPoints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Decisions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MeetingActionItem> ActionItems { get; init; } = Array.Empty<MeetingActionItem>();
    public IReadOnlyList<string> UnresolvedQuestions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Risks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ProcessSteps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Conclusions { get; init; } = Array.Empty<string>();
}

/// <summary>A per-chunk intermediate result (Map output) carrying its chunk index and time range.</summary>
public sealed record ChunkSummary(int ChunkIndex, TimeSpan Start, TimeSpan End, MeetingSummarySections Sections);

/// <summary>The final structured document: request metadata + merged sections, rendered to Markdown.</summary>
public sealed record MeetingSummaryDocument
{
    public required Guid SessionId { get; init; }
    public required MeetingType MeetingType { get; init; }
    public required string OutputLanguage { get; init; }
    public required string Model { get; init; }
    public required int PromptVersion { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required DateTimeOffset SessionDate { get; init; }
    public required int SegmentCount { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required MeetingSummarySections Sections { get; init; }
}

/// <summary>Progress update pushed to the UI during generation (localized at the view).</summary>
public sealed record MeetingSummaryProgress(MeetingSummaryPhase Phase, int Current, int Total);

/// <summary>The outcome of a completed generation.</summary>
public sealed record MeetingSummaryResult(MeetingSummaryDocument Document, string OutputPath);

/// <summary>A summary failure carrying a de-identified, retry-aware code (reuses the translation codes).</summary>
public sealed class MeetingSummaryException : Exception
{
    public MeetingSummaryException(TranslationErrorCode code, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    public TranslationErrorCode Code { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Summary-specific knobs, separate from the (reused) translation transport config. Chunk budget and
/// limits are bounded so a bad value can never produce zero/negative budgets or unbounded work.
/// </summary>
public sealed class MeetingSummaryOptions
{
    /// <summary>Optional summary model override; empty = reuse the translation model.</summary>
    public string Model { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 90;
    public int MaxRetries { get; set; } = 3;

    /// <summary>Approx. max characters of caption text per Map chunk (bounded 500..20000).</summary>
    public int ChunkBudgetChars { get; set; } = 6000;

    /// <summary>Max intermediate results merged per Reduce pass; more → hierarchical Reduce (bounded 2..50).</summary>
    public int ReduceGroupSize { get; set; } = 8;

    /// <summary>Hard cap on any single AI response we buffer (reject unbounded bodies).</summary>
    public long MaxResponseBytes { get; set; } = 1024 * 1024;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 5, 600));
    public int EffectiveMaxRetries => Math.Clamp(MaxRetries, 0, 10);
    public int EffectiveChunkBudget => Math.Clamp(ChunkBudgetChars, 500, 20000);
    public int EffectiveReduceGroupSize => Math.Clamp(ReduceGroupSize, 2, 50);
}
