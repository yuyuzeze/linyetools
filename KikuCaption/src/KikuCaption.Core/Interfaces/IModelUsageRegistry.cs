namespace KikuCaption.Core.Interfaces;

/// <summary>A held reference to a model in use. Disposing it releases the reference (R7B.1).</summary>
public interface IModelUsageLease : IDisposable
{
    /// <summary>The model this lease keeps in use (e.g. "whisper-small").</summary>
    string ModelId { get; }
}

/// <summary>
/// Per-model reference-counted usage tracking (R7B.1). Each entry point that loads a model
/// (realtime / WAV / prewarm / correction) takes a lease for its model id and disposes it when done —
/// even on exception, cancellation or worker crash. A model may not be replaced while ANY lease on it
/// is held; two models are tracked independently, so using small never blocks replacing medium.
/// </summary>
public interface IModelUsageRegistry
{
    /// <summary>Takes a reference on <paramref name="modelId"/>; dispose the lease to release it.</summary>
    IModelUsageLease Acquire(string modelId, Core.Models.WhisperModelPurpose purpose);

    /// <summary>True while at least one lease on <paramref name="modelId"/> is held.</summary>
    bool IsInUse(string modelId);
}
