using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Progress;
using KikuCaption.Core.Models;

namespace KikuCaption.ComponentManagement.Installing;

/// <summary>Localization keys for the install phases (resolved by the UI layer).</summary>
public static class ComponentInstallPhases
{
    public const string Preparing = "Install.Preparing";
    public const string Downloading = "Install.Downloading";
    public const string Extracting = "Install.Extracting";
    public const string Verifying = "Install.Verifying";
    public const string Installing = "Install.Installing";
    public const string Completed = "Install.Completed";
}

/// <summary>The result of a successful component install.</summary>
public sealed record ComponentInstallResult(
    string ComponentId, string InstallDirectory, long SizeBytes, ModelVerificationLevel VerificationLevel);

/// <summary>
/// Raised when an install is rejected or fails. <see cref="Code"/> is stable and localizable; the
/// message is non-sensitive. A failure of this kind NEVER leaves the previously installed component
/// damaged — the coordinator restores it.
/// </summary>
public sealed class ComponentInstallException : Exception
{
    public ComponentInstallException(string code, string message, Exception? inner = null) : base(message, inner)
        => Code = code;

    public string Code { get; }
}

/// <summary>Raised when the component is in use (e.g. the worker has the model loaded) and cannot be replaced.</summary>
public sealed class ComponentInUseException : Exception
{
    public ComponentInUseException(string message) : base(message) { }
}

/// <summary>
/// Verifies an EXTRACTED component before it is installed (R7B step: "model light-load verify"). The
/// default checks structure only; a model-aware implementation (e.g. a faster-whisper load) plugs in
/// here without changing the coordinator. Throws to reject; the extracted staging copy is discarded
/// and the existing install is left untouched.
/// </summary>
public interface IComponentVerifier
{
    /// <summary>
    /// Returns the achieved <see cref="ModelVerificationLevel"/> (StructureVerified or, when a real
    /// runtime load succeeded, RuntimeVerified). Throws to reject the extraction (Failed).
    /// </summary>
    Task<ModelVerificationLevel> VerifyAsync(RemoteComponent component, string extractedDirectory, CancellationToken cancellationToken);
}

/// <summary>Default verifier: structural only (the coordinator already checked requiredFiles).</summary>
public sealed class NoOpComponentVerifier : IComponentVerifier
{
    public Task<ModelVerificationLevel> VerifyAsync(RemoteComponent component, string extractedDirectory, CancellationToken cancellationToken)
        => Task.FromResult(ModelVerificationLevel.StructureVerified);
}

/// <summary>
/// Gate that decides whether a component may be REPLACED right now. The app implements this to block
/// replacing a model the Whisper worker currently has loaded (R7B requirement 7).
/// </summary>
public interface IComponentReplacementGuard
{
    /// <summary>Throws <see cref="ComponentInUseException"/> if the component cannot be replaced now.</summary>
    void EnsureCanReplace(RemoteComponent component);
}

/// <summary>Default guard: always allows replacement (used when nothing holds the component).</summary>
public sealed class AlwaysAllowReplacementGuard : IComponentReplacementGuard
{
    public void EnsureCanReplace(RemoteComponent component) { }
}

/// <summary>Installs a verified component into its target directory (R7B).</summary>
public interface IComponentInstaller
{
    Task<ComponentInstallResult> InstallAsync(
        RemoteComponent component,
        IProgress<ComponentInstallProgress>? progress,
        CancellationToken cancellationToken);
}
