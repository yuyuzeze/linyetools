using KikuCaption.Core.Enums;

namespace KikuCaption.Translation;

/// <summary>
/// A translation failure carrying a de-identified <see cref="TranslationErrorCode"/> and, for rate
/// limiting, an optional retry delay. The message is short and never contains the key, headers,
/// request body, or full response.
/// </summary>
public sealed class TranslationException : Exception
{
    public TranslationException(TranslationErrorCode code, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    public TranslationErrorCode Code { get; }

    /// <summary>Server-suggested delay before retrying (from <c>Retry-After</c>), if any.</summary>
    public TimeSpan? RetryAfter { get; }
}
