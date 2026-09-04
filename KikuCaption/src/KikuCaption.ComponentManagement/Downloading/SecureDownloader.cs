using System.Security.Cryptography;
using KikuCaption.ComponentManagement.Http;
using KikuCaption.ComponentManagement.Progress;
using KikuCaption.ComponentManagement.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.ComponentManagement.Downloading;

/// <summary>Outcome of a verified download.</summary>
public sealed record DownloadResult(string FilePath, long SizeBytes, string Sha256);

/// <summary>Thrown when a download is rejected by a safety check (size, hash, length mismatch).</summary>
public sealed class DownloadValidationException : Exception
{
    public DownloadValidationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

/// <summary>
/// Streams a file from an HTTPS URL into the download cache with defense in depth (R7A):
/// <list type="bullet">
/// <item>HTTPS required (unless the config opted into insecure HTTP).</item>
/// <item>Writes to a temporary <c>*.partial</c> file; the final name appears only after full verification.</item>
/// <item>Aborts if the stream exceeds the max size, even when Content-Length lied.</item>
/// <item>Verifies the server's Content-Length (when present) and the expected size match.</item>
/// <item>Computes SHA-256 while streaming and rejects a mismatch.</item>
/// <item>Deletes the temp file on any failure or cancellation, so nothing incomplete is mistaken for done.</item>
/// <item>Never overwrites an existing verified cache file with the same content hash.</item>
/// </list>
/// It only downloads + verifies into the cache; installing into a component directory is a later stage.
/// </summary>
public sealed class SecureDownloader
{
    private readonly IRawHttpTransport _transport;
    private readonly ILogger<SecureDownloader> _logger;
    private readonly int _maxRedirects;

    public SecureDownloader(IRawHttpTransport transport, ILogger<SecureDownloader>? logger = null, int maxRedirects = 5)
    {
        _transport = transport;
        _logger = logger ?? NullLogger<SecureDownloader>.Instance;
        _maxRedirects = maxRedirects;
    }

    /// <param name="url">HTTPS source URL.</param>
    /// <param name="expectedSha256">Required 64-char hex digest the download must match.</param>
    /// <param name="expectedSizeBytes">Expected size; also used to validate Content-Length. Null = unknown.</param>
    /// <param name="destinationFilePath">Final cached file path (created only after verification).</param>
    /// <param name="maxBytes">Hard size cap; a larger download is aborted and deleted.</param>
    /// <param name="allowInsecureHttp">Permit plain HTTP (trusted internal test only).</param>
    public async Task<DownloadResult> DownloadAsync(
        string url,
        string expectedSha256,
        long? expectedSizeBytes,
        string destinationFilePath,
        long maxBytes,
        bool allowInsecureHttp,
        IProgress<ComponentInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ResourceSecurity.IsAllowedUrl(url, allowInsecureHttp))
        {
            throw new DownloadValidationException("url", "Download URL must be an HTTPS URL.");
        }

        if (!ResourceSecurity.IsValidSha256(expectedSha256))
        {
            throw new DownloadValidationException("sha256", "A valid expected SHA-256 is required.");
        }

        // If a verified file with the same hash already exists, do not re-download or overwrite it.
        if (File.Exists(destinationFilePath) &&
            string.Equals(await ComputeSha256Async(destinationFilePath, cancellationToken).ConfigureAwait(false),
                expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Cache hit for {Url}; reusing verified file.", UrlSanitizer.Sanitize(url));
            return new DownloadResult(destinationFilePath, new FileInfo(destinationFilePath).Length, expectedSha256);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        var partialPath = destinationFilePath + ".partial";
        SafeDelete(partialPath);

        try
        {
            // R7A.1: controlled redirects — every hop re-validated, no HTTPS→HTTP downgrade.
            using var response = await SafeHttpRequester
                .GetWithControlledRedirectsAsync(_transport, new Uri(url), allowInsecureHttp, _maxRedirects, _logger, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? declared = response.Content.Headers.ContentLength;
            if (declared is { } d)
            {
                if (d > maxBytes)
                {
                    throw new DownloadValidationException("size", "Download exceeds the maximum allowed size.");
                }

                if (expectedSizeBytes is { } expected && d != expected)
                {
                    throw new DownloadValidationException("length", "Server Content-Length does not match the manifest size.");
                }
            }

            long total = declared ?? expectedSizeBytes ?? 0;
            string actualHash = await StreamToPartialAsync(response, partialPath, maxBytes, total, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadValidationException("sha256", "Downloaded file failed SHA-256 verification.");
            }

            long finalSize = new FileInfo(partialPath).Length;
            if (expectedSizeBytes is { } expectedFinal && finalSize != expectedFinal)
            {
                throw new DownloadValidationException("length", "Downloaded size does not match the manifest size.");
            }

            // Atomic publish: the final name exists only now that the content is fully verified.
            SafeDelete(destinationFilePath);
            File.Move(partialPath, destinationFilePath);
            _logger.LogInformation("Downloaded and verified {Url}.", UrlSanitizer.Sanitize(url));
            return new DownloadResult(destinationFilePath, finalSize, expectedSha256);
        }
        catch
        {
            // Any failure or cancellation: leave nothing that could be mistaken for a completed file.
            SafeDelete(partialPath);
            throw;
        }
    }

    private async Task<string> StreamToPartialAsync(
        HttpResponseMessage response, string partialPath, long maxBytes, long total,
        IProgress<ComponentInstallProgress>? progress, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = SHA256.Create();

        var buffer = new byte[81920];
        long completed = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            completed += read;
            if (completed > maxBytes)
            {
                throw new DownloadValidationException("size", "Download exceeds the maximum allowed size.");
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Report(new ComponentInstallProgress("Download.Downloading", completed, total > 0 ? total : null));
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
