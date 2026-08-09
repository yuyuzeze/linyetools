namespace KikuCaption.Core.Enums;

/// <summary>Lifecycle of a translation job persisted in the <c>TranslationJob</c> table (M6).</summary>
public enum TranslationJobState
{
    /// <summary>Durably queued, waiting for a worker.</summary>
    Pending,

    /// <summary>A worker is currently translating this segment.</summary>
    InProgress,

    /// <summary>Translation stored on the segment; terminal.</summary>
    Succeeded,

    /// <summary>A retryable failure occurred; will retry at <c>NextAttemptAt</c>.</summary>
    RetryScheduled,

    /// <summary>A non-retryable failure or retries exhausted; terminal. Original text is kept.</summary>
    FailedPermanent,

    /// <summary>Cancelled (e.g. app stopping); terminal for this run.</summary>
    Cancelled
}
