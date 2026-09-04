using System.IO.Compression;
using KikuCaption.ComponentManagement.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.ComponentManagement.Archives;

/// <summary>Thrown when an archive is rejected by a safety check (traversal, symlink, bomb limits).</summary>
public sealed class ArchiveValidationException : Exception
{
    public ArchiveValidationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

/// <summary>Result of a safe extraction.</summary>
public sealed record ExtractionResult(string DestinationDirectory, int FileCount, long TotalBytes);

/// <summary>
/// Extracts a ZIP with defense in depth (R7A):
/// <list type="bullet">
/// <item>Rejects absolute paths, drive/UNC prefixes and any <c>..</c> segment.</item>
/// <item>Verifies each resolved path stays inside the destination (Zip-Slip guard).</item>
/// <item>Rejects symlink / reparse-point entries (unix symlink mode bits).</item>
/// <item>Caps the entry count and the total uncompressed size (zip-bomb guard).</item>
/// <item>Extracts into a fresh temporary directory; publishing to a component directory is a later stage.</item>
/// </list>
/// </summary>
public sealed class SafeZipExtractor
{
    private const uint UnixSymlinkMode = 0xA000; // S_IFLNK in the high 16 bits of ExternalAttributes.
    private const uint UnixFileTypeMask = 0xF000;

    private readonly ILogger<SafeZipExtractor> _logger;

    public SafeZipExtractor(ILogger<SafeZipExtractor>? logger = null)
        => _logger = logger ?? NullLogger<SafeZipExtractor>.Instance;

    /// <param name="zipPath">The archive to extract.</param>
    /// <param name="destinationDirectory">A fresh directory to extract into (created if missing).</param>
    /// <param name="maxEntries">Maximum number of entries permitted.</param>
    /// <param name="maxTotalBytes">Maximum total uncompressed size permitted.</param>
    public ExtractionResult Extract(string zipPath, string destinationDirectory, int maxEntries, long maxTotalBytes,
        CancellationToken cancellationToken = default)
    {
        var destRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destRoot);

        using var archive = ZipFile.OpenRead(zipPath);

        if (archive.Entries.Count > maxEntries)
        {
            throw new ArchiveValidationException("entry-count",
                $"Archive has {archive.Entries.Count} entries (limit {maxEntries}).");
        }

        // Pre-flight: sum declared uncompressed sizes before writing anything (bomb guard).
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            declaredTotal += entry.Length;
            if (declaredTotal > maxTotalBytes)
            {
                throw new ArchiveValidationException("bomb",
                    "Archive uncompressed size exceeds the maximum allowed.");
            }
        }

        int fileCount = 0;
        long writtenTotal = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Directory entry (trailing separator, no name).
            bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

            if (IsSymlink(entry))
            {
                throw new ArchiveValidationException("symlink",
                    $"Archive entry '{entry.FullName}' is a symlink/reparse point.");
            }

            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':') ||
                HasTraversalSegment(entry.FullName))
            {
                throw new ArchiveValidationException("path",
                    $"Archive entry '{entry.FullName}' has an unsafe path.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));
            if (!ResourceSecurity.IsContainedWithin(destRoot, targetPath))
            {
                throw new ArchiveValidationException("zip-slip",
                    $"Archive entry '{entry.FullName}' escapes the destination.");
            }

            if (isDirectory || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // Extract with a running cap in case a declared length lied.
            writtenTotal += CopyEntryCapped(entry, targetPath, maxTotalBytes - writtenTotal, cancellationToken);
            fileCount++;
        }

        _logger.LogInformation("Extracted {Count} files ({Bytes} bytes) to a temporary directory.", fileCount, writtenTotal);
        return new ExtractionResult(destRoot, fileCount, writtenTotal);
    }

    private static long CopyEntryCapped(ZipArchiveEntry entry, string targetPath, long remainingBudget, CancellationToken ct)
    {
        using var source = entry.Open();
        using var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            written += read;
            if (written > remainingBudget)
            {
                throw new ArchiveValidationException("bomb", "Archive uncompressed size exceeds the maximum allowed.");
            }

            destination.Write(buffer, 0, read);
        }

        return written;
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        // Unix mode is stored in the high 16 bits of ExternalAttributes by most zip tools.
        uint mode = (uint)entry.ExternalAttributes >> 16;
        return (mode & UnixFileTypeMask) == UnixSymlinkMode;
    }

    private static bool HasTraversalSegment(string path)
    {
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }
}
