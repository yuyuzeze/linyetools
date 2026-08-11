namespace KikuCaption.App.ViewModels;

/// <summary>
/// Pure orchestration for the start-meeting dialog result (UI-R2 dialog-draft fix). It decides
/// whether a session should start and, critically, applies the draft to the live meeting state
/// <em>only</em> on a valid confirm — so cancel / Esc / window-close (any non-true dialog result)
/// leaves the main view model untouched, and a start is never attempted for an invalid target
/// (which would otherwise "save" a broken target).
/// </summary>
public static class MeetingStartCoordinator
{
    /// <summary>
    /// Returns true when the caller should proceed to start the session. Applies the draft to
    /// <paramref name="sink"/> only in that case.
    /// </summary>
    /// <param name="dialogResult">The dialog's <c>DialogResult</c>: true=confirm, false=cancel/Esc, null=closed.</param>
    public static bool ResolveStart(bool? dialogResult, StartMeetingDialogViewModel draft, IMeetingCaptureTargetSink sink)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(sink);

        if (dialogResult != true)
        {
            return false; // cancel / Esc / closed → do not touch the main view model
        }

        var target = draft.ToTarget();
        if (!target.IsValid)
        {
            return false; // never apply/save an invalid target, and do not start
        }

        sink.ApplyCaptureTarget(target);
        return true;
    }
}
