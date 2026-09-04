namespace KikuCaption.ComponentManagement.Progress;

/// <summary>
/// Progress for a long-running remote-resource operation (download / extract / verify). The
/// <see cref="PhaseKey"/> is a localization key (resolved by the UI layer in later stages), never a
/// user-facing sentence, so the same progress type stays UI- and language-agnostic.
/// </summary>
public sealed record ComponentInstallProgress(
    string PhaseKey,
    long CompletedBytes,
    long? TotalBytes)
{
    /// <summary>0.0–1.0 when a total is known; null while the total is unknown.</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes.Value, 0, 1) : null;
}
