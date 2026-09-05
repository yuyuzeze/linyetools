using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;

namespace KikuCaption.App.Services;

/// <summary>
/// Thread-safe, per-model reference-counted <see cref="IModelUsageRegistry"/> (R7B.1). Each
/// <see cref="Acquire"/> increments a per-model counter; disposing the returned lease decrements it
/// exactly once (idempotent, so double-dispose is safe). A model is "in use" while its count &gt; 0.
/// Small and medium have independent counters. No captions/prompts/PCM are ever recorded here.
/// </summary>
public sealed class ModelUsageRegistry : IModelUsageRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public IModelUsageLease Acquire(string modelId, WhisperModelPurpose purpose)
    {
        lock (_gate)
        {
            _counts[modelId] = _counts.TryGetValue(modelId, out var n) ? n + 1 : 1;
        }

        return new Lease(this, modelId);
    }

    public bool IsInUse(string modelId)
    {
        lock (_gate)
        {
            return _counts.TryGetValue(modelId, out var n) && n > 0;
        }
    }

    private void Release(string modelId)
    {
        lock (_gate)
        {
            if (_counts.TryGetValue(modelId, out var n))
            {
                if (n <= 1) { _counts.Remove(modelId); }
                else { _counts[modelId] = n - 1; }
            }
        }
    }

    private sealed class Lease : IModelUsageLease
    {
        private readonly ModelUsageRegistry _registry;
        private int _disposed;

        public Lease(ModelUsageRegistry registry, string modelId)
        {
            _registry = registry;
            ModelId = modelId;
        }

        public string ModelId { get; }

        public void Dispose()
        {
            // Release exactly once even if disposed twice (or via a finally after an exception).
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registry.Release(ModelId);
            }
        }
    }
}
