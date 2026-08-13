using System.IO;
using KikuCaption.Core.Interfaces;

namespace KikuCaption.App.Services;

/// <summary>Finds a complete local faster-whisper medium model without accessing the network.</summary>
public sealed class CorrectionModelLocator
{
    private static readonly string[] RequiredFiles =
        ["config.json", "model.bin", "tokenizer.json", "vocabulary.txt"];

    private readonly ISpeechOptionsProvider _speechOptions;
    private readonly long _minimumModelBytes;

    public CorrectionModelLocator(ISpeechOptionsProvider speechOptions, long minimumModelBytes = 1_000_000_000L)
    {
        _speechOptions = speechOptions;
        _minimumModelBytes = minimumModelBytes;
    }

    public CorrectionModelAvailability Check()
    {
        var cacheRoot = _speechOptions.ForLanguage("ja").ModelCacheDirectory;
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return new(false, null, null);

        foreach (var candidate in Candidates(Path.GetFullPath(cacheRoot)))
        {
            if (RequiredFiles.All(file => File.Exists(Path.Combine(candidate, file))) &&
                new FileInfo(Path.Combine(candidate, "model.bin")).Length >= _minimumModelBytes)
            {
                return new(true, candidate, cacheRoot);
            }
        }

        return new(false, null, cacheRoot);
    }

    private static IEnumerable<string> Candidates(string root)
    {
        // Simple directory intended for browser/manual downloads.
        yield return Path.Combine(root, "faster-whisper-medium");
        yield return Path.Combine(root, "medium");

        // Also accept KikuCaption/models/faster-whisper-medium when the configured
        // cache root is KikuCaption/models/whisper (the default development layout).
        var parent = Directory.GetParent(root)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "faster-whisper-medium");

        // Standard Hugging Face cache layout used by faster-whisper.
        var snapshots = Path.Combine(root, "models--Systran--faster-whisper-medium", "snapshots");
        if (Directory.Exists(snapshots))
        {
            foreach (var directory in Directory.EnumerateDirectories(snapshots)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
                yield return directory;
        }
    }
}

public sealed record CorrectionModelAvailability(bool IsAvailable, string? ModelPath, string? CacheRoot);
