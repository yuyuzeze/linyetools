using System.IO;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>
/// The single <see cref="IWhisperModelLocator"/> used by realtime, WAV, prewarm AND correction
/// (R7B.1). It prefers a MANAGED install (a model downloaded into the model cache with a valid
/// <c>.component.json</c> receipt): when present it returns the ABSOLUTE managed directory, which the
/// callers pass straight to the Python worker so faster-whisper loads that exact folder — never
/// "small" + a download root. When no managed install exists it falls back to the legacy model NAME so
/// the historical behavior is preserved. Logs only the model id + path category + level (never the
/// absolute path or any sensitive content).
/// </summary>
public sealed class WhisperModelLocator : IWhisperModelLocator
{
    private static readonly string[] RequiredFiles = ["config.json", "model.bin", "tokenizer.json", "vocabulary.txt"];

    private readonly ModelCatalog _catalog;
    private readonly long _minModelBytes;
    private readonly ILogger<WhisperModelLocator> _logger;

    public WhisperModelLocator(ModelCatalog catalog, ILogger<WhisperModelLocator> logger, long minModelBytes = 10_000_000L)
    {
        _catalog = catalog;
        _minModelBytes = minModelBytes;
        _logger = logger;
    }

    public WhisperModelResolution Resolve(string modelName, WhisperModelPurpose purpose)
    {
        var entry = MapToEntry(modelName);
        if (entry is null)
        {
            // Unknown model name → legacy passthrough (no managed install).
            return new WhisperModelResolution(modelName, modelName, string.Empty, false, ModelVerificationLevel.None, false);
        }

        var dir = _catalog.InstallDirectory(entry);
        if (!string.IsNullOrWhiteSpace(dir) && IsStructurallyPresent(dir!))
        {
            var receipt = InstallReceiptStore.TryRead(dir!);
            var level = receipt?.VerificationLevel ?? ModelVerificationLevel.StructureVerified;
            _logger.LogInformation("Model {Id} ({Purpose}) resolved to a MANAGED install ({Level}).",
                entry.ComponentId, purpose, level);
            // The managed absolute directory is what we hand to the worker.
            return new WhisperModelResolution(entry.ComponentId, dir!, dir!, true, level, true);
        }

        // Legacy fallback: the worker uses the model NAME (faster-whisper's own cache/download).
        _logger.LogInformation("Model {Id} ({Purpose}) has no managed install; using legacy name '{Name}'.",
            entry.ComponentId, purpose, modelName);
        return new WhisperModelResolution(entry.ComponentId, modelName, dir ?? string.Empty, false, ModelVerificationLevel.None, false);
    }

    private static ModelCatalogEntry? MapToEntry(string modelName)
    {
        // faster-whisper names or our managed directory names both map to the catalog.
        if (modelName.Contains("small", StringComparison.OrdinalIgnoreCase)) return ModelCatalog.Small;
        if (modelName.Contains("medium", StringComparison.OrdinalIgnoreCase)) return ModelCatalog.Medium;
        return null;
    }

    private bool IsStructurallyPresent(string dir)
    {
        try
        {
            if (!Directory.Exists(dir) || !RequiredFiles.All(f => File.Exists(Path.Combine(dir, f))))
            {
                return false;
            }

            return new FileInfo(Path.Combine(dir, "model.bin")).Length >= _minModelBytes;
        }
        catch
        {
            return false;
        }
    }
}
