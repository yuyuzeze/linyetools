namespace KikuCaption.Core.Models;

/// <summary>
/// An immutable snapshot of the translation direction taken when a meeting starts (UI-R4A §3). The
/// whole session translates in one direction, so a mid-session target change never affects the
/// running meeting, and crash recovery always knows the original source/target of each job.
///
/// <para>The source is always the meeting's recognition language; the target is user-configurable.
/// When source == target, translation is effectively disabled for the session (no jobs, no API
/// calls, no "failed" state) without overwriting the user's <see cref="Enabled"/> preference.</para>
///
/// <para>Lives in Core (no WPF / Translation dependency) so the queue interface and the App can both
/// use it.</para>
/// </summary>
public sealed record SessionTranslationOptions(
    string SourceLanguage,
    string TargetLanguage,
    bool Enabled,
    string Model,
    int PromptVersion)
{
    /// <summary>True when the source and target languages are the same (case-insensitive).</summary>
    public bool IsSameLanguage =>
        string.Equals(SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this session should actually translate: the user enabled it AND the direction is not
    /// same-language. Distinct from the persisted <see cref="Enabled"/> preference.
    /// </summary>
    public bool EffectiveEnabled => Enabled && !IsSameLanguage;
}
