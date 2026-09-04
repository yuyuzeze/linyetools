namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>Fetches and validates the remote manifest (R7A).</summary>
public interface IRemoteManifestClient
{
    /// <summary>
    /// Downloads the manifest at <paramref name="manifestUri"/> (HTTPS, size-capped) and returns the
    /// parsed, fully validated result. Throws <see cref="ManifestException"/> for an unsafe URL or a
    /// malformed/invalid document; network/timeout failures surface as the usual
    /// <see cref="HttpRequestException"/> / <see cref="OperationCanceledException"/> for the caller to
    /// handle — they never crash the app.
    /// </summary>
    Task<RemoteManifest> GetManifestAsync(Uri manifestUri, CancellationToken cancellationToken);

    /// <summary>
    /// Convenience wrapper over configuration: returns <c>null</c> WITHOUT any network request when
    /// remote resources are disabled or no manifest URL is set; otherwise fetches the configured
    /// manifest. This is the entry point callers should use so a disabled config never touches the
    /// network.
    /// </summary>
    Task<RemoteManifest?> GetConfiguredManifestAsync(CancellationToken cancellationToken);
}
