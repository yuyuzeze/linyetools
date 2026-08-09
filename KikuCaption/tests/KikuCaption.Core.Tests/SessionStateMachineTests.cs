using KikuCaption.Core.Enums;
using KikuCaption.Core.Session;
using Xunit;

namespace KikuCaption.Core.Tests;

public class SessionStateMachineTests
{
    [Fact] // 1: happy path Idle→Preflight→Starting→Running→Stopping→Completed→Idle
    public void FullHappyPath_Transitions()
    {
        var sm = new SessionStateMachine();
        Assert.Equal(SessionState.Idle, sm.State);
        Assert.True(sm.BeginPreflight());
        Assert.True(sm.TryTransition(SessionState.Starting));
        Assert.True(sm.TryTransition(SessionState.Running));
        Assert.True(sm.TryTransition(SessionState.Stopping));
        Assert.True(sm.TryTransition(SessionState.Completed));
        Assert.True(sm.TryTransition(SessionState.Idle));
    }

    [Fact] // 5: duplicate start rejected while busy
    public void DuplicateStart_Rejected()
    {
        var sm = new SessionStateMachine();
        Assert.True(sm.BeginPreflight());
        sm.TryTransition(SessionState.Starting);
        sm.TryTransition(SessionState.Running);

        Assert.False(sm.CanStart);
        Assert.False(sm.BeginPreflight()); // no second session
        Assert.Equal(SessionState.Running, sm.State);
    }

    [Fact] // 6: idempotent stop
    public void Stop_IsIdempotent()
    {
        var sm = new SessionStateMachine();
        sm.BeginPreflight();
        sm.TryTransition(SessionState.Starting);
        sm.TryTransition(SessionState.Running);

        Assert.True(sm.RequestStop());
        Assert.Equal(SessionState.Stopping, sm.State);
        Assert.True(sm.RequestStop()); // second stop = no-op success
        Assert.Equal(SessionState.Stopping, sm.State);
    }

    [Fact] // stop when idle is a harmless no-op
    public void Stop_WhenIdle_NoOp()
    {
        var sm = new SessionStateMachine();
        Assert.True(sm.RequestStop());
        Assert.Equal(SessionState.Idle, sm.State);
    }

    [Fact] // 4: cancel during start rolls back to Stopping
    public void CancelDuringStart_RollsBack()
    {
        var sm = new SessionStateMachine();
        sm.BeginPreflight();
        sm.TryTransition(SessionState.Starting);

        Assert.True(sm.TryTransition(SessionState.Stopping)); // rollback path
        Assert.True(sm.TryTransition(SessionState.Completed));
    }

    [Fact] // preflight block returns to Idle
    public void PreflightBlocked_ReturnsIdle()
    {
        var sm = new SessionStateMachine();
        sm.BeginPreflight();
        Assert.True(sm.TryTransition(SessionState.Idle));
        Assert.True(sm.CanStart);
    }

    [Fact] // fault from running, then reset
    public void Fault_FromRunning_ThenReset()
    {
        var sm = new SessionStateMachine();
        sm.BeginPreflight();
        sm.TryTransition(SessionState.Starting);
        sm.TryTransition(SessionState.Running);

        Assert.True(sm.TryTransition(SessionState.Faulted));
        Assert.True(sm.CanStart);                 // can start again after fault
        Assert.True(sm.TryTransition(SessionState.Idle));
    }

    [Theory] // illegal transitions are rejected
    [InlineData(SessionState.Running)]            // Idle→Running illegal
    [InlineData(SessionState.Completed)]          // Idle→Completed illegal
    [InlineData(SessionState.Stopping)]           // Idle→Stopping illegal
    public void IllegalFromIdle_Rejected(SessionState to)
    {
        var sm = new SessionStateMachine();
        Assert.False(sm.TryTransition(to));
        Assert.Equal(SessionState.Idle, sm.State);
    }

    [Fact] // StateChanged fires with (from,to)
    public void StateChanged_Fires()
    {
        var sm = new SessionStateMachine();
        (SessionState from, SessionState to)? seen = null;
        sm.StateChanged += (f, t) => seen = (f, t);
        sm.BeginPreflight();
        Assert.Equal((SessionState.Idle, SessionState.Preflight), seen);
    }

    [Fact] // recovering path is separate from live sessions
    public void Recovering_Path()
    {
        var sm = new SessionStateMachine();
        Assert.True(sm.TryTransition(SessionState.Recovering));
        Assert.False(sm.CanStart); // not startable mid-recovery
        Assert.True(sm.TryTransition(SessionState.Idle));
        Assert.True(sm.CanStart);
    }
}
