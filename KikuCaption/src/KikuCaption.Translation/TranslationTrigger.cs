using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Translation;

/// <summary>
/// The synchronous part of the enqueue trigger rules (M6 §2, UI-R4A): effective-enabled + final +
/// source language matches recognition + non-empty + not already translated. Same-language sessions
/// are rejected here (EffectiveEnabled is false). The "no active job already exists" check is async
/// and lives in the queue. Partials and a mismatched recognition language are rejected here.
/// </summary>
public static class TranslationTrigger
{
    public static bool ShouldEnqueue(TranscriptSegment segment, SessionTranslationOptions session)
    {
        if (!session.EffectiveEnabled)
        {
            return false; // disabled, or source == target (same-language: no job, no API call)
        }

        if (segment.Status is not (TranscriptStatus.Final or TranscriptStatus.TranslationFailed))
        {
            return false; // partials never; already-Translated never
        }

        if (!string.Equals(segment.Language, session.SourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return false; // the segment's language must be the session's source (recognition) language
        }

        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(segment.Translation))
        {
            return false; // already has a translation
        }

        return true;
    }
}
