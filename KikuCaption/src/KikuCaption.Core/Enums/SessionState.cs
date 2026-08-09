namespace KikuCaption.Core.Enums;

/// <summary>
/// The single, unified meeting-session lifecycle state (Milestone 7). One state machine governs the
/// whole start/stop flow so audio, recording, captions, storage and translation cannot form
/// conflicting sub-flows.
/// </summary>
public enum SessionState
{
    /// <summary>No session; ready to start.</summary>
    Idle,

    /// <summary>Running pre-start checks (environment, disk, targets).</summary>
    Preflight,

    /// <summary>Bringing subsystems up in order; a failure here rolls back.</summary>
    Starting,

    /// <summary>All required subsystems up; capturing/recognizing/recording.</summary>
    Running,

    /// <summary>Bringing subsystems down in order and finalizing outputs.</summary>
    Stopping,

    /// <summary>Session stopped cleanly; outputs finalized.</summary>
    Completed,

    /// <summary>Session ended due to an unrecoverable fault (original data is preserved).</summary>
    Faulted,

    /// <summary>Startup crash recovery in progress (not a live session).</summary>
    Recovering
}
