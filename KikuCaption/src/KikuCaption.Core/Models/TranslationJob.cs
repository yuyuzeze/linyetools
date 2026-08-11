using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Models;

/// <summary>
/// A durable translation task for one final segment (PROJECT.md 8.4/8.5, M6 §5). Exactly one active
/// (non-terminal) job may exist per segment. No credentials, headers, request/response bodies, or
/// full transcripts are ever stored on this record — only a de-identified <see cref="LastErrorCode"/>.
/// </summary>
public sealed record TranslationJob
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid SegmentId { get; init; }
    public required TranslationJobState State { get; init; }
    public int AttemptCount { get; init; }

    // UI-R4A: the direction is snapshotted onto the job so crash recovery re-translates in the
    // original direction even if the user later changes the target. Stable codes (ja/zh/en);
    // legacy jobs default to ja→zh, prompt version 1.
    public string SourceLanguage { get; init; } = "ja";
    public string TargetLanguage { get; init; } = "zh";
    public int PromptVersion { get; init; } = 1;

    /// <summary>
    /// The model snapshotted at meeting start. Empty on legacy (pre-v4) jobs — the queue then falls
    /// back to the live model and logs a sanitized warning; new jobs never enter the queue empty.
    /// </summary>
    public string Model { get; init; } = "";

    /// <summary>When a <see cref="TranslationJobState.RetryScheduled"/> job becomes eligible again.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>De-identified error category/short message, e.g. <c>RateLimited</c>. Never a body.</summary>
    public string? LastErrorCode { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
