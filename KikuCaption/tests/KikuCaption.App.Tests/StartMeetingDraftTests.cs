using KikuCaption.App.ViewModels;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// UI-R2 dialog-draft fix: the start dialog edits an independent draft and only applies it to the
/// live meeting state on a valid confirm. Cancel / Esc / close leave the main view model unchanged,
/// and an invalid target is never saved.
/// </summary>
public class StartMeetingDraftTests
{
    // Stands in for RealtimeCaptionViewModel's IMeetingCaptureTargetSink surface.
    private sealed class FakeSink : IMeetingCaptureTargetSink
    {
        public MeetingCaptureTarget CaptureTarget { get; private set; } = MeetingCaptureTarget.ScreenTarget;
        public int ApplyCount { get; private set; }
        public void ApplyCaptureTarget(MeetingCaptureTarget target) { CaptureTarget = target; ApplyCount++; }
    }

    private static StartMeetingDialogViewModel Draft(MeetingCaptureTarget initial)
        => new(initial, new[] { "Teams", "Chrome" }, @"C:\Meetings");

    [Fact] // draft copies the initial target and editing it does not touch the seed
    public void Draft_SeedsFromInitial_AndEditsStayLocal()
    {
        var draft = Draft(MeetingCaptureTarget.ScreenTarget);
        Assert.Equal("screen", draft.CaptureType);

        draft.CaptureType = "window";
        draft.SelectedWindow = "Teams";

        Assert.Equal("window", draft.ToTarget().CaptureType);
        Assert.Equal("Teams", draft.ToTarget().WindowTitle);
    }

    [Fact] // 1: changing the target then cancelling leaves the main view model unchanged
    public void Cancel_DoesNotChangeMainViewModel()
    {
        var sink = new FakeSink();
        var draft = Draft(sink.CaptureTarget);
        draft.CaptureType = "window";
        draft.SelectedWindow = "Chrome";

        var proceed = MeetingStartCoordinator.ResolveStart(dialogResult: false, draft, sink);

        Assert.False(proceed);
        Assert.Equal(0, sink.ApplyCount);
        Assert.Equal(MeetingCaptureTarget.ScreenTarget, sink.CaptureTarget); // still the original
    }

    [Fact] // 2: changing the target then confirming updates the main view model
    public void Confirm_UpdatesMainViewModel()
    {
        var sink = new FakeSink();
        var draft = Draft(sink.CaptureTarget);
        draft.CaptureType = "window";
        draft.SelectedWindow = "Chrome";

        var proceed = MeetingStartCoordinator.ResolveStart(dialogResult: true, draft, sink);

        Assert.True(proceed);
        Assert.Equal(1, sink.ApplyCount);
        Assert.Equal("window", sink.CaptureTarget.CaptureType);
        Assert.Equal("Chrome", sink.CaptureTarget.WindowTitle);
    }

    [Fact] // 3: Esc and window-close (false / null dialog result) behave exactly like cancel
    public void EscAndClose_AreTreatedAsCancel()
    {
        foreach (bool? result in new bool?[] { false, null })
        {
            var sink = new FakeSink();
            var draft = Draft(sink.CaptureTarget);
            draft.CaptureType = "window";
            draft.SelectedWindow = "Teams";

            var proceed = MeetingStartCoordinator.ResolveStart(result, draft, sink);

            Assert.False(proceed);
            Assert.Equal(0, sink.ApplyCount);
            Assert.Equal(MeetingCaptureTarget.ScreenTarget, sink.CaptureTarget);
        }
    }

    [Fact] // 4: confirming an invalid target (window capture, no window) does not save it or start
    public void InvalidTarget_IsNotSaved_AndDoesNotStart()
    {
        var sink = new FakeSink();
        var draft = Draft(sink.CaptureTarget);
        draft.CaptureType = "window";
        draft.SelectedWindow = null; // window capture with no window → invalid

        Assert.False(draft.CanStart);
        var proceed = MeetingStartCoordinator.ResolveStart(dialogResult: true, draft, sink);

        Assert.False(proceed);
        Assert.Equal(0, sink.ApplyCount);
        Assert.Equal(MeetingCaptureTarget.ScreenTarget, sink.CaptureTarget);
    }

    [Fact] // screen capture drops any stale window title when snapshotting
    public void ScreenCapture_HasNoWindowTitle()
    {
        var draft = Draft(new MeetingCaptureTarget("window", "Teams"));
        draft.CaptureType = "screen";

        Assert.Null(draft.ToTarget().WindowTitle);
        Assert.True(draft.CanStart);
    }
}
