using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.Core.Interfaces;

namespace KikuCaption.App.Services;

/// <summary>
/// R7B.1 replacement guard: refuses to replace a Whisper model while ANY entry point holds a lease on
/// it (realtime / WAV / prewarm / correction). Backed by the per-model reference-counted
/// <see cref="IModelUsageRegistry"/>, so replacing small only checks small and replacing medium only
/// checks medium.
/// </summary>
public sealed class WorkerModelReplacementGuard : IComponentReplacementGuard
{
    private readonly IModelUsageRegistry _registry;

    public WorkerModelReplacementGuard(IModelUsageRegistry registry) => _registry = registry;

    public void EnsureCanReplace(RemoteComponent component)
    {
        // Canonicalize so a case/alias variant of the manifest id cannot bypass the lease check.
        var canonical = CanonicalModelId.Resolve(component.Id, component.InstallDirectory);
        if (_registry.IsInUse(canonical))
        {
            throw new ComponentInUseException(
                $"Model '{canonical}' is currently in use; cannot replace it now.");
        }
    }
}
