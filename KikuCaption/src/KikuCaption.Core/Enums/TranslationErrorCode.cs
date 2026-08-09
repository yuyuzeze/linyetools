namespace KikuCaption.Core.Enums;

/// <summary>
/// De-identified classification of a translation failure. Only this short code (never the key,
/// headers, request body, or full response) is persisted/logged/shown (PROJECT.md 13, M6 §6).
/// </summary>
public enum TranslationErrorCode
{
    None,

    /// <summary>Request exceeded the configured timeout.</summary>
    Timeout,

    /// <summary>HTTP 429 — rate limited (honor Retry-After).</summary>
    RateLimited,

    /// <summary>HTTP 5xx — service temporarily unavailable.</summary>
    ServiceUnavailable,

    /// <summary>Transient transport/network error.</summary>
    Network,

    /// <summary>HTTP 401/403 — authentication/authorization failed.</summary>
    Auth,

    /// <summary>HTTP 400 — bad request (not retryable).</summary>
    BadRequest,

    /// <summary>Invalid model/deployment or invalid local configuration (not retryable).</summary>
    InvalidConfig,

    /// <summary>Response was missing, empty, non-JSON, or not valid translation content.</summary>
    InvalidResponse,

    /// <summary>Input exceeded the maximum allowed length (not retryable).</summary>
    InputTooLong,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>Unclassified failure.</summary>
    Unknown
}

/// <summary>Retry policy helpers for <see cref="TranslationErrorCode"/>.</summary>
public static class TranslationErrorCodes
{
    /// <summary>True when the error is transient and the job should be retried with backoff.</summary>
    public static bool IsRetryable(this TranslationErrorCode code) => code switch
    {
        TranslationErrorCode.Timeout => true,
        TranslationErrorCode.RateLimited => true,
        TranslationErrorCode.ServiceUnavailable => true,
        TranslationErrorCode.Network => true,
        _ => false
    };
}
