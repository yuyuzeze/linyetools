using KikuCaption.App.Localization;
using KikuCaption.Core.Enums;

namespace KikuCaption.App.Tray;

/// <summary>The distinct actions the tray menu / double-click can request. Shell-agnostic.</summary>
public enum TrayCommand
{
    StartSession,
    StopSession,
    ToggleOverlay,
    OpenSettings,
    OpenMainWindow,
    Exit
}

/// <summary>One rendered tray-menu entry: which command, its localized text, and its live state.</summary>
public sealed record TrayMenuItem(TrayCommand Command, string Text, bool Enabled, bool Visible);

/// <summary>
/// Pure builder for the tray context menu and tooltip (UI-R5B). It derives item enablement and text
/// purely from the session state (mirroring <c>SessionStateMachine</c> rules — it never bypasses the
/// machine) and the overlay visibility, and it localizes every string. No WinForms, no side effects,
/// no session/window state — fully unit-testable.
/// </summary>
public static class TrayMenuModel
{
    // Stay within the conservative NotifyIcon tooltip limit (the classic 63-char cap) for maximum
    // compatibility — the real strings are ~25 chars, so this never truncates in practice.
    private const int TooltipMaxLength = 63;

    public static IReadOnlyList<TrayMenuItem> Build(SessionState state, bool overlayVisible, LocalizationService loc)
    {
        // Mirrors SessionStateMachine.CanStart / CanStop so the menu matches the real gate.
        bool canStart = state is SessionState.Idle or SessionState.Completed or SessionState.Faulted;
        bool canStop = state is SessionState.Preflight or SessionState.Starting or SessionState.Running;

        return new List<TrayMenuItem>
        {
            new(TrayCommand.StartSession, loc["Tray.StartSession"], Enabled: canStart, Visible: true),
            new(TrayCommand.StopSession, loc["Tray.StopSession"], Enabled: canStop, Visible: true),
            new(TrayCommand.ToggleOverlay,
                overlayVisible ? loc["Tray.HideOverlay"] : loc["Tray.ShowOverlay"], Enabled: true, Visible: true),
            new(TrayCommand.OpenSettings, loc["Tray.OpenSettings"], Enabled: true, Visible: true),
            new(TrayCommand.OpenMainWindow, loc["Tray.OpenMainWindow"], Enabled: true, Visible: true),
            new(TrayCommand.Exit, loc["Tray.Exit"], Enabled: true, Visible: true)
        };
    }

    /// <summary>The tray tooltip: "KikuCaption · &lt;status&gt;", plus the running time while recording.</summary>
    public static string Tooltip(SessionState state, TimeSpan? elapsed, LocalizationService loc)
    {
        var statusKey = state switch
        {
            SessionState.Idle or SessionState.Completed => "Tray.Status.Idle",
            SessionState.Preflight or SessionState.Starting or SessionState.Recovering => "Tray.Status.Starting",
            SessionState.Running => "Tray.Status.Recording",
            SessionState.Stopping => "Tray.Status.Stopping",
            SessionState.Faulted => "Tray.Status.Error",
            _ => "Tray.Status.Idle"
        };

        var text = $"{loc["Common.AppName"]} · {loc[statusKey]}";
        if (state == SessionState.Running && elapsed is { } e)
        {
            text += " " + FormatElapsed(e);
        }

        return text.Length <= TooltipMaxLength ? text : text[..TooltipMaxLength];
    }

    private static string FormatElapsed(TimeSpan e)
        => e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
}
