using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// The ONE place every entry point (realtime / WAV / prewarm / correction) turns a model name into a
/// path (R7B.1). Guarantees the status page and the worker never use two different path schemes.
/// </summary>
public interface IWhisperModelLocator
{
    /// <summary>Resolves a model name ("small"/"medium") for a purpose, preferring a managed install.</summary>
    WhisperModelResolution Resolve(string modelName, WhisperModelPurpose purpose);
}
