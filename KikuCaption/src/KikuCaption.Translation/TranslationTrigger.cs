using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Translation;

/// <summary>
/// The synchronous part of the enqueue trigger rules (M6 §2): enabled + final + source language +
/// non-empty + not already translated. The "no active job already exists" check is async and lives
/// in the queue. Partials and the wrong recognition language are rejected here.
/// </summary>
public static class TranslationTrigger
{
    public static bool ShouldEnqueue(TranscriptSegment segment, TranslationOptions options)
    {
        if (!options.Enabled)
        {
            return false;
        }

        if (segment.Status is not (TranscriptStatus.Final or TranscriptStatus.TranslationFailed))
        {
            return false; // partials never; already-Translated never
        }

        if (!string.Equals(segment.Language, options.SourceLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return false; // e.g. Chinese recognition mode must not call JA→ZH
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
