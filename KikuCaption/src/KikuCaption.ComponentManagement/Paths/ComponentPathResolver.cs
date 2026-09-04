using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Security;

namespace KikuCaption.ComponentManagement.Paths;

/// <summary>
/// The single source of truth for remote-resource paths (R7A), so dev and release builds never grow
/// two competing path schemes again. Resolves the user-writable download cache and, from a component's
/// validated RELATIVE install directory, the absolute install directory under a fixed components root.
/// It computes paths only — it neither downloads nor installs.
/// </summary>
public sealed class ComponentPathResolver
{
    private readonly string _componentsRoot;
    private readonly string _downloadsCache;

    public ComponentPathResolver(string componentsRootDirectory, string downloadsCacheDirectory)
    {
        _componentsRoot = Path.GetFullPath(componentsRootDirectory);
        _downloadsCache = Path.GetFullPath(downloadsCacheDirectory);
    }

    /// <summary>%LOCALAPPDATA%\KikuCaption\downloads for the cache; components root = the app directory.</summary>
    public static ComponentPathResolver CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appRoot = Path.Combine(localAppData, "KikuCaption");
        return new ComponentPathResolver(
            componentsRootDirectory: AppContext.BaseDirectory,
            downloadsCacheDirectory: Path.Combine(appRoot, "downloads"));
    }

    /// <summary>The user-writable download cache directory (created on demand by callers).</summary>
    public string DownloadsCacheDirectory => _downloadsCache;

    /// <summary>The root under which every component's relative install directory is resolved.</summary>
    public string ComponentsRoot => _componentsRoot;

    /// <summary>
    /// Resolves a component's absolute install directory from its validated relative
    /// <see cref="RemoteComponent.InstallDirectory"/>, re-checking that it is safe and stays inside the
    /// components root (defense in depth even though the parser already validated it).
    /// </summary>
    public string ResolveInstallDirectory(RemoteComponent component)
    {
        if (!ResourceSecurity.IsSafeRelativeInstallPath(component.InstallDirectory))
        {
            throw new ManifestException("path", $"Unsafe install directory for component '{component.Id}'.");
        }

        var full = Path.GetFullPath(Path.Combine(_componentsRoot, component.InstallDirectory));
        if (!ResourceSecurity.IsContainedWithin(_componentsRoot, full))
        {
            throw new ManifestException("path", $"Install directory for component '{component.Id}' escapes the root.");
        }

        return full;
    }

    /// <summary>
    /// A deterministic cache file path for a component, named from its id + version (never from the
    /// URL, so a query string can never influence the filename). Callers append the download there.
    /// </summary>
    public string GetCachedArchivePath(RemoteComponent component)
        => Path.Combine(_downloadsCache, CacheFileName(component.Id, component.Version));

    /// <summary>
    /// The deterministic cache file name for a component (id + version, sanitized). Exposed as a
    /// static so the manifest validator can detect two components that would map to the SAME cache
    /// file (R7A.1) using exactly this rule.
    /// </summary>
    public static string CacheFileName(string id, string version)
        => $"{MakeFileNameSafe(id)}-{MakeFileNameSafe(version)}.zip";

    private static string MakeFileNameSafe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim('_');
        return cleaned.Length == 0 ? "component" : cleaned;
    }
}
