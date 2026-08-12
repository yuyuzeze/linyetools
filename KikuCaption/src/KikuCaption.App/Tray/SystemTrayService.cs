using System.Windows;
using System.Windows.Threading;
using KikuCaption.App.Localization;
using KikuCaption.App.Navigation;
using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Tray;

/// <summary>The tray coordinator lifecycle + window-event hooks the main window calls into.</summary>
public interface ISystemTrayService : IDisposable
{
    /// <summary>Shows the icon and starts reacting to state/overlay/language changes. Call once.</summary>
    void Start();

    /// <summary>Handle a window minimize. Returns true if it was hidden to the tray.</summary>
    bool HandleMinimize();

    /// <summary>Handle the window's X. Hides to tray (CloseToTray) or runs the real-exit flow.</summary>
    Task HandleWindowCloseAsync();

    /// <summary>The explicit "Exit" flow: confirm-if-running → safe stop → ordered shutdown.</summary>
    Task RequestExitAsync();

    /// <summary>True once a real exit is underway (lets the window's Closing allow the final close).</summary>
    bool IsExiting { get; }
}

/// <summary>
/// System-tray coordinator (UI-R5B). Owns no session/window/overlay logic of its own — it drives the
/// EXISTING flows through <see cref="ITraySessionSource"/> (which wraps the real StopCommand /
/// ToggleOverlayCommand and goes through the SessionStateMachine), navigation, the shared
/// <see cref="IMeetingLauncher"/>, and an injected shutdown action. The NotifyIcon shell and the
/// window are behind <see cref="ITrayIconAdapter"/> / <see cref="IMainWindowController"/>, so this
/// class is unit-testable with fakes. Every tray callback is marshalled to the WPF Dispatcher before
/// touching UI state. Only numbers / localized status are shown — never caption text, paths, or secrets.
/// </summary>
public sealed class SystemTrayService : ISystemTrayService
{
    private readonly ITrayIconAdapter _adapter;
    private readonly IMainWindowController _window;
    private readonly ITraySessionSource _session;
    private readonly INavigationService _navigation;
    private readonly IMeetingLauncher _launcher;
    private readonly LocalizationService _loc;
    private readonly Func<bool> _minimizeToTray;
    private readonly Func<bool> _closeToTray;
    private readonly Func<bool> _confirmExitWhileRunning;
    private readonly Action _shutdown;
    private readonly ILogger _logger;

    private DispatcherTimer? _tooltipTimer;
    private DateTime? _sessionStartUtc;
    private SessionState _lastState = SessionState.Idle;
    private bool _hintShown;
    private bool _started;
    private bool _disposed;
    private volatile bool _exiting;

    public SystemTrayService(
        ITrayIconAdapter adapter,
        IMainWindowController window,
        ITraySessionSource session,
        INavigationService navigation,
        IMeetingLauncher launcher,
        LocalizationService loc,
        Func<bool> minimizeToTray,
        Func<bool> closeToTray,
        Func<bool> confirmExitWhileRunning,
        Action shutdown,
        ILogger<SystemTrayService> logger)
    {
        _adapter = adapter;
        _window = window;
        _session = session;
        _navigation = navigation;
        _launcher = launcher;
        _loc = loc;
        _minimizeToTray = minimizeToTray;
        _closeToTray = closeToTray;
        _confirmExitWhileRunning = confirmExitWhileRunning;
        _shutdown = shutdown;
        _logger = logger;
    }

    public bool IsExiting => _exiting;

    public void Start()
    {
        if (_started)
        {
            return; // exactly one icon for the app lifetime
        }
        _started = true;

        _adapter.CommandInvoked += cmd => Dispatch(() => OnCommand(cmd));
        _adapter.DoubleClicked += () => Dispatch(_window.RestoreFromTray);
        _session.Changed += OnSessionChangedRaised;
        _loc.LanguageChanged += OnLanguageChanged;

        _adapter.Visible = true;
        Refresh();

        _tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();
        _tooltipTimer.Start();
    }

    // ---- window hooks ----------------------------------------------------

    public bool HandleMinimize()
    {
        if (!_minimizeToTray())
        {
            return false; // standard Windows minimize (stays on the taskbar)
        }

        _window.HideToTray();
        if (!_hintShown)
        {
            _hintShown = true; // one-time, non-blocking — never nag on every minimize
            _adapter.ShowBalloon(_loc["Common.AppName"], _loc["Tray.MinimizedHint"]);
        }

        return true;
    }

    public async Task HandleWindowCloseAsync()
    {
        if (_exiting)
        {
            return; // a real exit is already underway; let the close proceed
        }

        if (_closeToTray())
        {
            _window.HideToTray(); // X hides to tray — the session keeps running, nothing is released
            return;
        }

        await RequestExitAsync().ConfigureAwait(true);
    }

    // ---- explicit exit ---------------------------------------------------

    public async Task RequestExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        // A running session must be confirmed before a real exit (data is already durable either way).
        if (_session.IsRunning && !_confirmExitWhileRunning())
        {
            return; // user cancelled → the meeting continues
        }

        _exiting = true; // set BEFORE stopping so a re-entrant Closing allows the final close

        try
        {
            await _session.StopAsync().ConfigureAwait(true); // full safe stop even if the window is hidden
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Safe stop during exit failed; data was already persisted.");
        }

        Dispose();   // remove the icon + stop the timer BEFORE shutdown (no ghost icon)
        _shutdown(); // App wires: close overlay → Application.Shutdown (releases host services)
    }

    // ---- commands --------------------------------------------------------

    private void OnCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.StartSession:
                _window.RestoreFromTray();
                _ = _launcher.StartFromDialogAsync(); // safe path: restore + open the start dialog
                break;
            case TrayCommand.StopSession:
                _ = _session.StopAsync();
                Refresh(); // reflect "stopping" immediately (prevents a second stop)
                break;
            case TrayCommand.ToggleOverlay:
                _session.ToggleOverlay();
                break;
            case TrayCommand.OpenSettings:
                _window.RestoreFromTray();
                _navigation.Navigate(PageKey.Settings);
                break;
            case TrayCommand.OpenMainWindow:
                _window.RestoreFromTray(); // keep the current page (no forced navigation)
                break;
            case TrayCommand.Exit:
                _ = RequestExitAsync();
                break;
        }
    }

    // ---- state → menu/tooltip -------------------------------------------

    private void OnSessionChangedRaised() => Dispatch(OnSessionChanged);

    private void OnSessionChanged()
    {
        var state = _session.State;
        if (state == SessionState.Running && _sessionStartUtc is null)
        {
            _sessionStartUtc = DateTime.UtcNow;
        }
        else if (state is SessionState.Idle or SessionState.Completed or SessionState.Faulted)
        {
            _sessionStartUtc = null;
        }

        // Notify only on a critical session fault (never per caption/translation). Generic, non-
        // sensitive message — never the raw error text, which could contain a path.
        if (state == SessionState.Faulted && _lastState != SessionState.Faulted)
        {
            _adapter.ShowBalloon(_loc["Common.AppName"], _loc["Tray.SessionError"]);
        }
        _lastState = state;

        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        RebuildMenu();
        UpdateTooltip();
    }

    private void RebuildMenu()
        => _adapter.SetMenu(TrayMenuModel.Build(_session.State, _session.OverlayVisible, _loc));

    private void UpdateTooltip()
    {
        var state = _session.State;
        TimeSpan? elapsed = state == SessionState.Running && _sessionStartUtc is { } t ? DateTime.UtcNow - t : null;
        _adapter.SetTooltip(TrayMenuModel.Tooltip(state, elapsed, _loc));
    }

    // ---- lifecycle -------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
        {
            return; // idempotent
        }
        _disposed = true;

        if (_tooltipTimer is not null)
        {
            _tooltipTimer.Stop();
            _tooltipTimer = null;
        }

        _session.Changed -= OnSessionChangedRaised;
        _loc.LanguageChanged -= OnLanguageChanged;

        try { _adapter.Dispose(); } catch { /* best effort — never throw on teardown */ }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
