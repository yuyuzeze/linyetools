using KikuCaption.Core.Enums;

namespace KikuCaption.App.Tray;

/// <summary>
/// The exact session surface the tray needs, isolated from the heavy <c>RealtimeCaptionViewModel</c>
/// so the coordinator is unit-testable. The production adapter wraps the view model and reuses its
/// existing StopCommand / ToggleOverlayCommand — the tray never re-implements start/stop/overlay logic.
/// </summary>
public interface ITraySessionSource
{
    bool IsRunning { get; }
    SessionState State { get; }
    bool OverlayVisible { get; }
    bool CanStop { get; }

    /// <summary>Runs the existing unified safe-stop (no duplicated stop order).</summary>
    Task StopAsync();

    /// <summary>Toggles the overlay via the existing command (keeps every entry point in sync).</summary>
    void ToggleOverlay();

    /// <summary>Raised when the session state or the overlay visibility changes.</summary>
    event Action? Changed;
}

/// <summary>
/// The system-tray shell (the NotifyIcon), abstracted so the tray business logic is testable without
/// a real desktop. The production implementation wraps <c>System.Windows.Forms.NotifyIcon</c>; tests
/// use a fake that records calls and raises the events. All members are called on the UI thread.
/// </summary>
public interface ITrayIconAdapter : IDisposable
{
    /// <summary>Whether the notification-area icon is shown.</summary>
    bool Visible { get; set; }

    /// <summary>Sets the hover tooltip (already clamped to the platform length limit by the caller).</summary>
    void SetTooltip(string text);

    /// <summary>Rebuilds the right-click context menu from the current model.</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> items);

    /// <summary>Shows a non-blocking balloon notification.</summary>
    void ShowBalloon(string title, string text);

    /// <summary>Raised when a menu item is chosen (on the UI thread).</summary>
    event Action<TrayCommand>? CommandInvoked;

    /// <summary>Raised on a left double-click of the icon (on the UI thread).</summary>
    event Action? DoubleClicked;
}

/// <summary>
/// Window operations the tray needs, abstracted from the concrete <c>MainWindow</c> so the tray
/// coordinator is testable. Implemented by the main window; all methods run on the UI thread.
/// </summary>
public interface IMainWindowController
{
    /// <summary>Hide the window to the tray: hidden + removed from the taskbar. The session keeps running.</summary>
    void HideToTray();

    /// <summary>Restore + activate the window (Normal, back in the taskbar, brought to the foreground).</summary>
    void RestoreFromTray();
}
