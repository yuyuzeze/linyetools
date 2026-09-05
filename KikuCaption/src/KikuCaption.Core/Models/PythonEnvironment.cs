namespace KikuCaption.Core.Models;

/// <summary>How a resolved worker Python was found (R7C).</summary>
public enum PythonEnvSource
{
    None,
    Config,       // explicit PythonExecutable / VenvPath in configuration
    ManagedVenv,  // %LOCALAPPDATA%\KikuCaption\python\venv
    DevVenv,      // python/whisper_worker/.venv in a dev checkout
    LegacyVenv,   // some other previously-created venv
    SystemForCreate // a compatible system Python — usable ONLY to create a venv, not as the worker env
}

/// <summary>The overall state of the speech-recognition Python environment.</summary>
public enum PythonEnvState
{
    /// <summary>No compatible system Python found — cannot install without one.</summary>
    NoSystemPython,

    /// <summary>A compatible system Python exists but no worker venv yet — offer Install.</summary>
    NoVenv,

    /// <summary>A worker venv exists but is broken/incomplete — offer Repair.</summary>
    VenvBroken,

    /// <summary>A healthy worker venv with the required packages — ready.</summary>
    VenvHealthy
}

/// <summary>Phases of the install/repair state machine (also localization keys via "PyInstall.&lt;Phase&gt;").</summary>
public enum PythonInstallPhase
{
    Idle,
    Inspecting,
    CreatingVenv,
    InstallingPackages,
    VerifyingImports,
    VerifyingWorker,
    VerifyingModels,
    Committing,
    Completed,
    Cancelled,
    Failed
}

/// <summary>Result of probing one candidate Python interpreter.</summary>
public sealed record PythonProbeInfo(string Version, bool Is64Bit, bool HasVenvModule, bool Compatible);

/// <summary>A discovered candidate interpreter (priority: lower = preferred).</summary>
public sealed record PythonCandidate(string Executable, int Priority, PythonProbeInfo? Info);

/// <summary>What the locator resolved for every Python consumer (worker/probe/verifier/…).</summary>
public sealed record PythonEnvironmentResolution(
    string? WorkerPython,       // absolute venv python to run the worker/model, or null
    PythonEnvSource WorkerSource,
    string? CreatePython,       // a compatible system python for creating a venv, or null
    string? VenvPath,
    bool VenvHealthy);

/// <summary>A snapshot of the environment for the UI (no sensitive full paths).</summary>
public sealed record PythonEnvironmentStatus(
    PythonEnvState State,
    string? SystemPythonVersion,
    string? VenvPythonVersion,
    string? FasterWhisperVersion,
    string? CTranslate2Version)
{
    public bool CanInstall => State is PythonEnvState.NoVenv or PythonEnvState.VenvBroken && SystemPythonVersion is not null;
    public bool CanRepair => State is PythonEnvState.VenvBroken;
}

/// <summary>Progress of an install/repair.</summary>
public sealed record PythonInstallProgress(PythonInstallPhase Phase, string? Detail, double? Fraction);

/// <summary>Outcome of an install/repair.</summary>
public sealed record PythonEnvironmentInstallResult(
    bool Success,
    PythonInstallPhase FinalPhase,
    string? ErrorCode,
    string? PythonVersion,
    string? FasterWhisperVersion,
    string? CTranslate2Version);

/// <summary>
/// Durable phases of the environment SWITCH, recorded in install-state.json so a crash / power loss
/// between the two directory renames is recoverable at next startup (R7C.1).
/// </summary>
public enum PythonInstallTxPhase
{
    Preparing,
    StagingVerified,
    OldEnvironmentBackedUp,
    NewEnvironmentActivated,
    ActiveEnvironmentVerified,
    Completed
}

/// <summary>
/// One persisted install-transaction journal entry (install-state.json). Structural facts ONLY — never
/// secrets, proxy, package-index URLs, or pip output (R7C.1).
/// </summary>
public sealed record PythonInstallJournalEntry(
    int SchemaVersion,
    string OperationId,
    PythonInstallTxPhase Phase,
    string ManagedPath,
    string StagingPath,
    string? BackupPath,
    string StartedAt);

/// <summary>What crash-recovery did to the managed environment at startup (R7C.1).</summary>
public enum PythonRecoveryAction
{
    None,                // no interrupted transaction — nothing to reconcile
    KeptManaged,         // the managed venv was valid — cleaned any stray staging/backup
    RestoredBackup,      // managed missing → the backup was restored to managed
    PromotedStaging,     // managed missing/invalid + a fully runtime-verified staging → promoted
    QuarantinedStaging,  // an unverified staging was set aside (never promoted to managed)
    RolledBackToBackup,  // the managed venv was invalid → the backup was restored
    JournalCorrupted     // install-state.json was unreadable → quarantined + conservative recovery
}

/// <summary>Result of a crash-recovery pass.</summary>
public sealed record PythonRecoveryResult(PythonRecoveryAction Action, bool ManagedUsable);
