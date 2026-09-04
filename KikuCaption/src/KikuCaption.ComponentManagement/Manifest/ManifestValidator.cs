using KikuCaption.ComponentManagement.Paths;
using KikuCaption.ComponentManagement.Security;

namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>
/// Cross-cutting manifest validation beyond single-field parsing (R7A.1):
/// <list type="bullet">
/// <item><b>requiredFiles</b>: every entry is a safe relative path that resolves INSIDE the component
/// install directory, with no duplicate after normalization.</item>
/// <item><b>component collection</b>: ids are unique case-insensitively; install directories are unique
/// and never nested one inside another; and no two components map to the same download cache file.</item>
/// <item><b>identity</b>: the manifest's product + channel match what the app expects (else no download).</item>
/// </list>
/// A synthetic absolute root is used only to reason about relative containment; nothing touches disk.
/// </summary>
public static class ManifestValidator
{
    // Case comparison policy for identity + path uniqueness. Product/channel and Windows paths are
    // compared CASE-INSENSITIVELY (documented, R7A.1); ids likewise (a folder/service key).
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    // A synthetic root that never exists on disk, used purely for relative-path reasoning.
    private static readonly string SyntheticRoot =
        Path.Combine(Path.GetTempPath(), "__kiku_manifest_check__");

    /// <summary>Validates a component's requiredFiles; throws <see cref="ManifestException"/> on violation.</summary>
    public static void ValidateRequiredFiles(string componentId, string installDirectory, IReadOnlyList<string> files)
    {
        var installFull = Path.GetFullPath(Path.Combine(SyntheticRoot, ToNativeRelative(installDirectory)));
        var seen = new HashSet<string>(Ci);

        foreach (var file in files)
        {
            // Empty / absolute / drive / UNC / "..": rejected exactly like an install path.
            if (!ResourceSecurity.IsSafeRelativeInstallPath(file))
            {
                throw new ManifestException("required-file", $"component '{componentId}' has an unsafe requiredFiles entry.");
            }

            var resolved = Path.GetFullPath(Path.Combine(installFull, ToNativeRelative(file)));
            if (!ResourceSecurity.IsContainedWithin(installFull, resolved))
            {
                throw new ManifestException("required-file", $"component '{componentId}' requiredFiles entry escapes its install directory.");
            }

            if (!seen.Add(NormalizeRelative(file)))
            {
                throw new ManifestException("required-file", $"component '{componentId}' has a duplicate requiredFiles entry.");
            }
        }
    }

    /// <summary>Validates the whole component set for id / directory / cache-name conflicts.</summary>
    public static void ValidateComponentCollection(IReadOnlyList<RemoteComponent> components)
    {
        var ids = new HashSet<string>(Ci);
        var cacheNames = new HashSet<string>(Ci);
        var normalizedDirs = new List<(string Dir, string Id)>();

        foreach (var c in components)
        {
            if (!ids.Add(c.Id))
            {
                throw new ManifestException("duplicate-id", $"Duplicate component id '{c.Id}' (case-insensitive).");
            }

            var cache = ComponentPathResolver.CacheFileName(c.Id, c.Version);
            if (!cacheNames.Add(cache))
            {
                throw new ManifestException("cache-collision", $"Component '{c.Id}' maps to a cache file already used by another component.");
            }

            var dir = NormalizeRelative(c.InstallDirectory);
            foreach (var (existing, existingId) in normalizedDirs)
            {
                if (Ci.Equals(dir, existing))
                {
                    throw new ManifestException("duplicate-dir", $"Components '{c.Id}' and '{existingId}' share an install directory.");
                }

                if (IsNestedWithin(dir, existing) || IsNestedWithin(existing, dir))
                {
                    throw new ManifestException("nested-dir", $"Install directories of '{c.Id}' and '{existingId}' are nested.");
                }
            }

            normalizedDirs.Add((dir, c.Id));
        }
    }

    /// <summary>Verifies the manifest identity against the expected product + channel (case-insensitive).</summary>
    public static void ValidateIdentity(RemoteManifest manifest, string expectedProduct, string expectedChannel)
    {
        if (!Ci.Equals(manifest.Product, expectedProduct))
        {
            throw new ManifestException("product", $"Manifest product '{manifest.Product}' does not match expected '{expectedProduct}'.");
        }

        if (!Ci.Equals(manifest.Channel, expectedChannel))
        {
            throw new ManifestException("channel", $"Manifest channel '{manifest.Channel}' does not match expected '{expectedChannel}'.");
        }
    }

    // "a/b" is nested within "a" (child), but NOT within "ab". Both inputs are already normalized.
    private static bool IsNestedWithin(string child, string parent)
        => child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    // Lowercase, forward-slash, no trailing slash — a canonical form for comparing relative paths.
    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static string ToNativeRelative(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
