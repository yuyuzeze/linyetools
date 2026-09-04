namespace KikuCaption.ComponentManagement.Configuration;

/// <summary>
/// Configuration for the unified remote-resource system (R7A). Only the manifest address and a few
/// safety limits are configured here — never a URL that carries a token, and never a credential.
/// When <see cref="Enabled"/> is false the app performs no network request of any kind.
/// </summary>
public sealed class RemoteResourceOptions
{
    /// <summary>Master switch. When false, no manifest is fetched and no component is downloaded.</summary>
    public bool Enabled { get; init; }

    /// <summary>HTTPS URL of the remote manifest. Ignored when <see cref="Enabled"/> is false.</summary>
    public string? ManifestUrl { get; init; }

    /// <summary>
    /// The product name the manifest MUST declare (identity check, R7A.1). A manifest whose
    /// <c>product</c> does not match is rejected and NO component is downloaded. Compared
    /// case-insensitively (see <see cref="Manifest.ManifestValidator"/>).
    /// </summary>
    public string ExpectedProduct { get; init; } = "KikuMemo";

    /// <summary>
    /// The release channel the manifest MUST declare (identity check, R7A.1). A mismatch is rejected.
    /// Compared case-insensitively.
    /// </summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Maximum number of HTTP redirects to follow, each re-validated (R7A.1).</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Timeout for the manifest request and each component download.</summary>
    public int RequestTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Whether plain-HTTP URLs are permitted. Default false: only HTTPS is accepted, for the manifest
    /// and for every download/component URL it references. Intended only for a trusted internal test.
    /// </summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Hard cap on the manifest document size (defends against a runaway response).</summary>
    public long MaxManifestBytes { get; init; } = 1 * 1024 * 1024; // 1 MB

    /// <summary>Hard cap on any single downloaded file. A download exceeding this is aborted+deleted.</summary>
    public long MaxDownloadBytes { get; init; } = 3L * 1024 * 1024 * 1024; // 3 GB

    /// <summary>Maximum number of entries a component archive may contain (zip-bomb guard).</summary>
    public int MaxArchiveEntries { get; init; } = 4096;

    /// <summary>Maximum total uncompressed size a component archive may expand to (zip-bomb guard).</summary>
    public long MaxExtractedBytes { get; init; } = 4L * 1024 * 1024 * 1024; // 4 GB
}

/// <summary>
/// Application self-update configuration (consumed by R7D). Defined now so the config schema is
/// stable; R7A does not act on it.
/// </summary>
public sealed class UpdateOptions
{
    public bool Enabled { get; init; }
    public string Channel { get; init; } = "stable";
    public bool CheckOnStartup { get; init; }
    public bool AutoDownload { get; init; }
}
