namespace KikuCaption.Translation;

/// <summary>Exponential backoff with jitter and Retry-After support (M6 §6). Pure and testable.</summary>
public static class TranslationBackoff
{
    /// <summary>
    /// Delay before retry <paramref name="attempt"/> (1-based). Base doubles each attempt (capped),
    /// plus 0–50% jitter. If the server sent <paramref name="retryAfter"/>, it wins (bounded).
    /// </summary>
    public static TimeSpan ComputeDelay(
        int attempt,
        TimeSpan? retryAfter,
        Random rng,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
    {
        var b = baseDelay ?? TimeSpan.FromSeconds(1);
        var max = maxDelay ?? TimeSpan.FromSeconds(60);

        double expSeconds = b.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        expSeconds = Math.Min(expSeconds, max.TotalSeconds);
        double jitter = rng.NextDouble() * (expSeconds * 0.5); // 0..50%
        var computed = TimeSpan.FromSeconds(expSeconds + jitter);

        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            var capped = ra <= max ? ra : max;
            return capped > computed ? capped : computed;
        }

        return computed;
    }
}
