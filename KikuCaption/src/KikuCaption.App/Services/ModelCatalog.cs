using System.IO;

namespace KikuCaption.App.Services;

/// <summary>
/// One installable speech model the environment page can manage (R7B). <see cref="ComponentId"/> is
/// the manifest component id; <see cref="InstallSubdirectory"/> is where it lives under the model
/// cache root (matching the layout CorrectionModelLocator already checks for medium). Small drives
/// real-time recognition; medium drives the post-meeting corrected captions.
/// </summary>
public sealed record ModelCatalogEntry(string ComponentId, string InstallSubdirectory, ModelRole Role, string NameKey);

public enum ModelRole
{
    /// <summary>Real-time recognition model (small).</summary>
    Realtime,

    /// <summary>Post-meeting corrected-caption model (medium).</summary>
    Correction
}

/// <summary>The fixed set of models KikuMemo knows how to download + install (R7B).</summary>
public sealed class ModelCatalog
{
    /// <summary>The model cache root (e.g. &lt;repo&gt;/models/whisper), or null when unknown.</summary>
    public string? ModelCacheRoot { get; }

    public ModelCatalog(string? modelCacheRoot) => ModelCacheRoot = modelCacheRoot;

    public static readonly ModelCatalogEntry Small =
        new("whisper-small", "faster-whisper-small", ModelRole.Realtime, "Models.Small");

    public static readonly ModelCatalogEntry Medium =
        new("whisper-medium", "faster-whisper-medium", ModelRole.Correction, "Models.Medium");

    public IReadOnlyList<ModelCatalogEntry> Entries { get; } = new[] { Small, Medium };

    /// <summary>The absolute install directory for an entry, or null when the cache root is unknown.</summary>
    public string? InstallDirectory(ModelCatalogEntry entry)
        => string.IsNullOrWhiteSpace(ModelCacheRoot)
            ? null
            : Path.Combine(ModelCacheRoot, entry.InstallSubdirectory);
}
