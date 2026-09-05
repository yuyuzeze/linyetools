using System.IO;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Progress;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>Presence + verification of an installed model (R7B.1 adds level + version from the receipt).</summary>
public sealed record ModelInstallStatus(
    bool Installed, long SizeBytes, string? Path, ModelVerificationLevel VerificationLevel, string? Version);

/// <summary>Coordinates model status + one-click install for the environment page (R7B).</summary>
public interface IModelDownloadCoordinator
{
    /// <summary>True when a manifest source is actually configured (else Download must be disabled).</summary>
    bool RemoteConfigured { get; }

    /// <summary>Structural presence + receipt (verification level/version) check. No network.</summary>
    ModelInstallStatus GetStatus(ModelCatalogEntry entry);

    /// <summary>
    /// Fetches the configured manifest, finds the entry's component, and installs it (download → SHA →
    /// safe extract → requiredFiles → verify → atomic install). Throws
    /// <see cref="ComponentInstallException"/> with a stable code on any failure.
    /// </summary>
    Task InstallAsync(ModelCatalogEntry entry, IProgress<ComponentInstallProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IModelDownloadCoordinator"/>. Composes the ComponentManagement manifest client +
/// installer with the local <see cref="ModelCatalog"/>. It never logs URLs/credentials/captions; only
/// component ids and phase transitions.
/// </summary>
public sealed class ModelDownloadCoordinator : IModelDownloadCoordinator
{
    private static readonly string[] RequiredFiles = ["config.json", "model.bin", "tokenizer.json", "vocabulary.txt"];

    private readonly ModelCatalog _catalog;
    private readonly IRemoteManifestClient _manifestClient;
    private readonly IComponentInstaller _installer;
    private readonly RemoteResourceOptions _options;
    private readonly long _minModelBytes;
    private readonly ILogger<ModelDownloadCoordinator> _logger;

    public ModelDownloadCoordinator(
        ModelCatalog catalog,
        IRemoteManifestClient manifestClient,
        IComponentInstaller installer,
        RemoteResourceOptions options,
        ILogger<ModelDownloadCoordinator> logger,
        long minModelBytes = 50_000_000L)
    {
        _catalog = catalog;
        _manifestClient = manifestClient;
        _installer = installer;
        _options = options;
        _minModelBytes = minModelBytes;
        _logger = logger;
    }

    public bool RemoteConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ManifestUrl);

    public ModelInstallStatus GetStatus(ModelCatalogEntry entry)
    {
        var dir = _catalog.InstallDirectory(entry);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return new ModelInstallStatus(false, 0, dir, ModelVerificationLevel.None, null);
        }

        try
        {
            if (!RequiredFiles.All(f => File.Exists(Path.Combine(dir, f))))
            {
                return new ModelInstallStatus(false, 0, dir, ModelVerificationLevel.None, null);
            }

            long modelBin = new FileInfo(Path.Combine(dir, "model.bin")).Length;
            if (modelBin < _minModelBytes)
            {
                return new ModelInstallStatus(false, modelBin, dir, ModelVerificationLevel.None, null);
            }

            long total = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

            // R7B.1: the receipt is the source of truth for version + verification level. When it is
            // missing/inconsistent, the files are present so we report StructureVerified (re-verify path).
            var receipt = InstallReceiptStore.TryRead(dir);
            var level = receipt?.VerificationLevel ?? ModelVerificationLevel.StructureVerified;
            return new ModelInstallStatus(true, total, dir, level, receipt?.Version);
        }
        catch
        {
            return new ModelInstallStatus(false, 0, dir, ModelVerificationLevel.None, null);
        }
    }

    public async Task InstallAsync(ModelCatalogEntry entry, IProgress<ComponentInstallProgress>? progress, CancellationToken cancellationToken)
    {
        var manifest = await _manifestClient.GetConfiguredManifestAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            throw new ComponentInstallException("no-manifest", "Remote resources are not configured.");
        }

        var component = manifest.Components.FirstOrDefault(
            c => string.Equals(c.Id, entry.ComponentId, StringComparison.OrdinalIgnoreCase));
        if (component is null)
        {
            throw new ComponentInstallException("not-in-manifest", $"Component '{entry.ComponentId}' is not in the manifest.");
        }

        _logger.LogInformation("Installing model component {Id}.", entry.ComponentId);
        await _installer.InstallAsync(component, progress, cancellationToken).ConfigureAwait(false);
    }
}
