using KikuCaption.App.ViewModels;
using KikuCaption.Audio.Diagnostics;
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
        public MeetingAudioOptions AudioOptions { get; private set; } = MeetingAudioOptions.Default;
        public int ApplyCount { get; private set; }
        public int AudioApplyCount { get; private set; }
        public void ApplyCaptureTarget(MeetingCaptureTarget target) { CaptureTarget = target; ApplyCount++; }
        public void ApplyAudioOptions(MeetingAudioOptions options) { AudioOptions = options; AudioApplyCount++; }
    }

    private static StartMeetingDialogViewModel Draft(MeetingCaptureTarget initial)
        => new(initial, new[] { "Teams", "Chrome" }, @"C:\Meetings");

    private static readonly AudioCaptureDeviceInfo DevA = new("id-a", "Headset Mic", IsDefaultCommunications: false);
    private static readonly AudioCaptureDeviceInfo DevB = new("id-b", "Laptop Mic", IsDefaultCommunications: true);

    private static StartMeetingDialogViewModel AudioDraft(MeetingAudioOptions audio,
        System.Collections.Generic.IReadOnlyList<AudioCaptureDeviceInfo>? devices = null)
        => new(MeetingCaptureTarget.ScreenTarget, System.Array.Empty<string>(), @"C:\Meetings",
               audio, devices ?? new[] { DevA, DevB });

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

    // ---- UI-R5A audio inputs --------------------------------------------

    [Fact] // scenario 1: a null saved device id selects the default communications device
    public void NullDevice_SelectsDefaultCommunications()
    {
        var draft = AudioDraft(new MeetingAudioOptions(true, true, null));
        Assert.Equal("id-b", draft.SelectedMicDeviceId); // DevB is the default-communications device
    }

    [Fact] // scenario 2: a saved (still-present) device id is restored
    public void SavedDevice_IsRestored()
    {
        var draft = AudioDraft(new MeetingAudioOptions(true, true, "id-a"));
        Assert.Equal("id-a", draft.SelectedMicDeviceId);
    }

    [Fact] // scenario 3: a saved device that has vanished falls back to the default (never a dead id)
    public void VanishedDevice_FallsBackToDefault()
    {
        var draft = AudioDraft(new MeetingAudioOptions(true, true, "id-gone"));
        Assert.Equal("id-b", draft.SelectedMicDeviceId);
    }

    [Fact] // scenario 21: microphone off → the audio options carry no device id
    public void MicOff_DropsDeviceId()
    {
        var draft = AudioDraft(new MeetingAudioOptions(true, false, "id-a"));
        Assert.False(draft.RecordMicrophone);
        Assert.Null(draft.ToAudioOptions().MicrophoneDeviceId);
        Assert.True(draft.CanStart); // system audio alone is enough
    }

    [Fact] // a meeting requires at least one audio input
    public void NoInput_CannotStart()
    {
        var draft = AudioDraft(new MeetingAudioOptions(false, false, null));
        Assert.False(draft.HasAnyInput);
        Assert.False(draft.CanStart);
        Assert.True(draft.ShowNoInputWarning);
    }

    [Fact] // confirming applies the audio options to the sink alongside the target
    public void Confirm_AppliesAudioOptions()
    {
        var sink = new FakeSink();
        var draft = AudioDraft(new MeetingAudioOptions(true, true, "id-a"));

        var proceed = MeetingStartCoordinator.ResolveStart(dialogResult: true, draft, sink);

        Assert.True(proceed);
        Assert.Equal(1, sink.AudioApplyCount);
        Assert.True(sink.AudioOptions.RecordSystemAudio);
        Assert.True(sink.AudioOptions.RecordMicrophone);
        Assert.Equal("id-a", sink.AudioOptions.MicrophoneDeviceId);
    }

    [Fact] // confirming with no audio input never starts and applies nothing
    public void Confirm_NoInput_DoesNotStart()
    {
        var sink = new FakeSink();
        var draft = AudioDraft(new MeetingAudioOptions(false, false, null));

        var proceed = MeetingStartCoordinator.ResolveStart(dialogResult: true, draft, sink);

        Assert.False(proceed);
        Assert.Equal(0, sink.ApplyCount);
        Assert.Equal(0, sink.AudioApplyCount);
    }
}
