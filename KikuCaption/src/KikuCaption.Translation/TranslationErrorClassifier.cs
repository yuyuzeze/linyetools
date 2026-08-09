using System.Net;
using KikuCaption.Core.Enums;

namespace KikuCaption.Translation;

/// <summary>Maps HTTP status codes to de-identified, retry-aware error codes (M6 §6).</summary>
public static class TranslationErrorClassifier
{
    public static TranslationErrorCode FromStatus(HttpStatusCode status) => (int)status switch
    {
        400 => TranslationErrorCode.BadRequest,
        401 => TranslationErrorCode.Auth,
        403 => TranslationErrorCode.Auth,
        404 => TranslationErrorCode.InvalidConfig, // wrong endpoint / missing deployment
        408 => TranslationErrorCode.Timeout,
        429 => TranslationErrorCode.RateLimited,
        >= 500 and <= 599 => TranslationErrorCode.ServiceUnavailable,
        _ => TranslationErrorCode.Unknown
    };
}
