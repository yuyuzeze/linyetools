namespace KikuCaption.Core.Enums;

/// <summary>
/// Lifecycle status of a transcript segment (PROJECT.md 8.1).
/// </summary>
public enum TranscriptStatus
{
    /// <summary>Provisional text still being refined; UI-only, never persisted as final.</summary>
    Partial,

    /// <summary>Confirmed original-language text; persisted immediately.</summary>
    Final,

    /// <summary>A final segment that has been translated to the target language.</summary>
    Translated,

    /// <summary>A final segment whose translation attempt failed (original text is kept).</summary>
    TranslationFailed
}
