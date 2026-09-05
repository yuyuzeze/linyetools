namespace KikuCaption.App.Services;

/// <summary>
/// Maps any model identifier (a manifest component id, an install-directory name, or a raw model name)
/// to ONE stable canonical id — "whisper-small" or "whisper-medium" (R7B.1/R7B.2). Both the usage
/// leases and the replacement guard canonicalize before touching the registry, so a case difference
/// ("Whisper-Small") or an alias ("small", "faster-whisper-small") can never bypass the per-model
/// occupancy protection.
/// </summary>
public static class CanonicalModelId
{
    public const string Small = "whisper-small";
    public const string Medium = "whisper-medium";

    /// <summary>Canonicalizes an id (+optional install dir). Falls back to the lowercased id.</summary>
    public static string Resolve(string? componentId, string? installDirectory = null)
    {
        var probe = ((componentId ?? string.Empty) + " " + (installDirectory ?? string.Empty));
        if (probe.Contains("small", StringComparison.OrdinalIgnoreCase)) return Small;
        if (probe.Contains("medium", StringComparison.OrdinalIgnoreCase)) return Medium;
        return (componentId ?? string.Empty).ToLowerInvariant();
    }
}
