using KikuCaption.Core.Enums;

namespace KikuCaption.Translation;

/// <summary>
/// A state transition for one segment's translation, surfaced so the UI can update the matching
/// card in place (by <see cref="SegmentId"/>) without creating duplicates or forcing a scroll.
/// </summary>
public sealed record TranslationOutcome(
    Guid SegmentId,
    TranslationJobState State,
    string? Translation,
    TranslationErrorCode ErrorCode);
