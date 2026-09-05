using KikuCaption.ComponentManagement.Archives;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Downloading;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Paths;
using KikuCaption.ComponentManagement.Progress;
using KikuCaption.ComponentManagement.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.ComponentManagement.Installing;

/// <summary>
/// Composes the R7A primitives into the R7B install pipeline:
/// <c>guard → download → SHA-256 → safe extract (temp) → requiredFiles → verify → atomic install</c>.
///
/// Safety guarantees:
/// <list type="bullet">
/// <item>The download is HTTPS-only, size-capped and SHA-256 verified (<see cref="SecureDownloader"/>).</item>
/// <item>Extraction goes to a fresh STAGING directory beside the install directory (same volume, so the
/// final swap is a rename) and is Zip-Slip / bomb / symlink guarded (<see cref="SafeZipExtractor"/>).</item>
/// <item>requiredFiles must all be present and non-empty; then the injected verifier runs.</item>
/// <item>The swap backs up the existing install, moves staging in, and RESTORES the backup on any error —
/// so a failed install never damages a previously good component.</item>
/// <item>The replacement guard can veto (e.g. the worker is using the model) BEFORE anything downloads.</item>
/// </list>
/// Nothing here loads a DLL/EXE or runs a command from the manifest.
/// </summary>
public sealed class ComponentInstallCoordinator : IComponentInstaller
{
    private readonly SecureDownloader _downloader;
    private readonly SafeZipExtractor _extractor;
    private readonly ComponentPathResolver _paths;
    private readonly IComponentVerifier _verifier;
    private readonly IComponentReplacementGuard _guard;
    private readonly RemoteResourceOptions _options;
    private readonly ILogger<ComponentInstallCoordinator> _logger;

    public ComponentInstallCoordinator(
        SecureDownloader downloader,
        SafeZipExtractor extractor,
        ComponentPathResolver paths,
        RemoteResourceOptions options,
        IComponentVerifier? verifier = null,
        IComponentReplacementGuard? guard = null,
        ILogger<ComponentInstallCoordinator>? logger = null)
    {
        _downloader = downloader;
        _extractor = extractor;
        _paths = paths;
        _options = options;
        _verifier = verifier ?? new NoOpComponentVerifier();
        _guard = guard ?? new AlwaysAllowReplacementGuard();
        _logger = logger ?? NullLogger<ComponentInstallCoordinator>.Instance;
    }

    public async Task<ComponentInstallResult> InstallAsync(
        RemoteComponent component, IProgress<ComponentInstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Preparing, 0, null));

        // 1) The worker/holder may forbid replacing this component right now — check before downloading.
        _guard.EnsureCanReplace(component);

        var installDir = _paths.ResolveInstallDirectory(component);
        var installParent = Directory.GetParent(installDir)?.FullName
            ?? throw new ComponentInstallException("path", "Install directory has no parent.");
        Directory.CreateDirectory(installParent);

        // Staging sits beside the install dir (same volume) so the final swap is an atomic rename.
        var staging = Path.Combine(installParent, $".staging-{Guid.NewGuid():N}");
        var archivePath = _paths.GetCachedArchivePath(component);

        try
        {
            // 2) Download + SHA-256 (SecureDownloader streams to *.partial, verifies, publishes to cache).
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Downloading, 0, component.SizeBytes));
            await _downloader.DownloadAsync(
                component.Url, component.Sha256, component.SizeBytes, archivePath,
                _options.MaxDownloadBytes, _options.AllowInsecureHttp, progress, cancellationToken).ConfigureAwait(false);

            // 3) Safe extract to staging.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Extracting, 0, null));
            _extractor.Extract(archivePath, staging, _options.MaxArchiveEntries, _options.MaxExtractedBytes, cancellationToken);

            // 4) requiredFiles must all be present + non-empty.
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Verifying, 0, null));
            VerifyRequiredFiles(component, staging);

            // 5) Injected verifier (structural, or a real faster-whisper runtime load when a venv exists).
            var level = await _verifier.VerifyAsync(component, staging, cancellationToken).ConfigureAwait(false);

            // 6) Write the install receipt INTO staging BEFORE the swap, so the installed directory and
            //    its .component.json land together atomically (and a rollback removes both).
            InstallReceiptStore.Write(staging, new InstallReceipt(
                InstallReceiptStore.CurrentSchemaVersion, component.Id, component.Version, component.Sha256,
                DateTime.UtcNow.ToString("O"), level));

            // 7) Atomic install with backup/rollback.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Installing, 0, null));
            AtomicSwap(staging, installDir);

            long size = DirectorySize(installDir);
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Completed, size, size));
            _logger.LogInformation("Installed component {Id} ({Bytes} bytes, {Level}).", component.Id, size, level);
            return new ComponentInstallResult(component.Id, installDir, size, level);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Install of component {Id} was cancelled.", component.Id);
            throw;
        }
        catch (ComponentInstallException)
        {
            throw;
        }
        catch (DownloadValidationException ex)
        {
            throw new ComponentInstallException(ex.Code, "Download validation failed.", ex);
        }
        catch (ArchiveValidationException ex)
        {
            throw new ComponentInstallException(ex.Code, "Archive validation failed.", ex);
        }
        finally
        {
            TryDeleteDirectory(staging); // staging is only ever a scratch copy
        }
    }

    private static void VerifyRequiredFiles(RemoteComponent component, string extractedDir)
    {
        foreach (var relative in component.RequiredFiles)
        {
            var full = Path.GetFullPath(Path.Combine(extractedDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!ResourceSecurity.IsContainedWithin(extractedDir, full))
            {
                throw new ComponentInstallException("required-file", $"requiredFiles entry '{relative}' escapes the extraction.");
            }

            if (!File.Exists(full) || new FileInfo(full).Length == 0)
            {
                throw new ComponentInstallException("required-file", $"requiredFiles entry '{relative}' is missing or empty.");
            }
        }
    }

    // Backs up any existing install, moves staging in, and restores the backup on any failure.
    private void AtomicSwap(string staging, string installDir)
    {
        string? backup = null;
        if (Directory.Exists(installDir))
        {
            backup = installDir + $".bak-{Guid.NewGuid():N}";
            Directory.Move(installDir, backup);
        }

        try
        {
            Directory.Move(staging, installDir);
        }
        catch (Exception ex)
        {
            // Roll back: remove a partial destination and restore the previous install verbatim.
            TryDeleteDirectory(installDir);
            if (backup is not null && Directory.Exists(backup))
            {
                Directory.Move(backup, installDir);
            }

            throw new ComponentInstallException("install", "Failed to move the verified files into place.", ex);
        }

        if (backup is not null)
        {
            TryDeleteDirectory(backup);
        }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
