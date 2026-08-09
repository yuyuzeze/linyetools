using KikuCaption.Core.Enums;

namespace KikuCaption.Core.Session;

/// <summary>
/// The unified session lifecycle state machine (Milestone 7). Pure and thread-safe: only legal
/// transitions succeed, duplicate starts are rejected, and stop is idempotent. It holds no
/// subsystem references — the orchestrator drives it and reacts to <see cref="StateChanged"/>.
/// </summary>
public sealed class SessionStateMachine
{
    private readonly object _gate = new();

    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>Raised (under no lock) after a successful transition: (from, to).</summary>
    public event Action<SessionState, SessionState>? StateChanged;

    /// <summary>A new session may begin only from a terminal/idle state.</summary>
    public bool CanStart => State is SessionState.Idle or SessionState.Completed or SessionState.Faulted;

    /// <summary>A stop is meaningful only while a session is coming up or running.</summary>
    public bool CanStop => State is SessionState.Preflight or SessionState.Starting or SessionState.Running;

    /// <summary>True while a session occupies the pipeline (blocks a second start).</summary>
    public bool IsBusy => State is SessionState.Preflight or SessionState.Starting or SessionState.Running or SessionState.Stopping;

    /// <summary>Attempts a transition; returns false (no change) if it is not legal.</summary>
    public bool TryTransition(SessionState to)
    {
        SessionState from;
        lock (_gate)
        {
            if (!IsLegal(State, to))
            {
                return false;
            }

            from = State;
            State = to;
        }

        StateChanged?.Invoke(from, to);
        return true;
    }

    /// <summary>Begins the flow (Idle→Preflight). Returns false if a session is already busy.</summary>
    public bool BeginPreflight() => TryTransition(SessionState.Preflight);

    /// <summary>Idempotent stop request: transitions to Stopping if busy, otherwise no-op true.</summary>
    public bool RequestStop()
    {
        lock (_gate)
        {
            if (State == SessionState.Stopping || !CanStop)
            {
                // Already stopping, or nothing to stop — idempotent success.
                return State is SessionState.Stopping or SessionState.Idle or SessionState.Completed or SessionState.Faulted;
            }
        }

        return TryTransition(SessionState.Stopping);
    }

    private static bool IsLegal(SessionState from, SessionState to) => (from, to) switch
    {
        (SessionState.Idle, SessionState.Preflight) => true,
        (SessionState.Idle, SessionState.Recovering) => true,

        (SessionState.Preflight, SessionState.Starting) => true,
        (SessionState.Preflight, SessionState.Idle) => true,       // blocked/aborted preflight
        (SessionState.Preflight, SessionState.Stopping) => true,   // cancelled during preflight
        (SessionState.Preflight, SessionState.Faulted) => true,

        (SessionState.Starting, SessionState.Running) => true,
        (SessionState.Starting, SessionState.Stopping) => true,    // cancel/rollback mid-start
        (SessionState.Starting, SessionState.Faulted) => true,

        (SessionState.Running, SessionState.Stopping) => true,
        (SessionState.Running, SessionState.Faulted) => true,

        (SessionState.Stopping, SessionState.Completed) => true,
        (SessionState.Stopping, SessionState.Faulted) => true,

        (SessionState.Completed, SessionState.Idle) => true,       // reset for next session
        (SessionState.Faulted, SessionState.Idle) => true,

        (SessionState.Recovering, SessionState.Idle) => true,

        _ => false
    };
}
