using System.ComponentModel;
using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.Navigation;
using KikuCaption.App.Services;
using KikuCaption.App.Tray;
using KikuCaption.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R5B: tray coordinator behaviour (minimize/close/exit/commands/menu) with fake shells.</summary>
public class SystemTrayServiceTests
{
    // ---- fakes -----------------------------------------------------------

    private sealed class FakeTray : ITrayIconAdapter
    {
        public bool Visible { get; set; }
        public string Tooltip = string.Empty;
        public IReadOnlyList<TrayMenuItem> Menu = System.Array.Empty<TrayMenuItem>();
        public int Balloons;
        public int DisposeCount;
        public event Action<TrayCommand>? CommandInvoked;
        public event Action? DoubleClicked;

        public void SetTooltip(string text) => Tooltip = text;
        public void SetMenu(IReadOnlyList<TrayMenuItem> items) => Menu = items;
        public void ShowBalloon(string title, string text) => Balloons++;
        public void Dispose() => DisposeCount++;

        public void RaiseCommand(TrayCommand c) => CommandInvoked?.Invoke(c);
        public void RaiseDoubleClick() => DoubleClicked?.Invoke();
        public TrayMenuItem Item(TrayCommand c) => Menu.First(i => i.Command == c);
    }

    private sealed class FakeWindow : IMainWindowController
    {
        public int HideCount;
        public int RestoreCount;
        public void HideToTray() => HideCount++;
        public void RestoreFromTray() => RestoreCount++;
    }

    private sealed class FakeSession : ITraySessionSource
    {
        public bool IsRunning { get; set; }
        public SessionState State { get; set; } = SessionState.Idle;
        public bool OverlayVisible { get; set; }
        public bool CanStop { get; set; }
        public int StopCount;
        public int ToggleCount;
        public event Action? Changed;

        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public void ToggleOverlay() { ToggleCount++; OverlayVisible = !OverlayVisible; Changed?.Invoke(); }
        public void Raise() => Changed?.Invoke();
    }

    private sealed class FakeLauncher : IMeetingLauncher
    {
        public int StartCount;
        public Task StartFromDialogAsync() { StartCount++; return Task.CompletedTask; }
    }

    private sealed class FakeNav : INavigationService
    {
        public PageKey Navigated = PageKey.Home;
        public int NavigateCount;
        public object? CurrentViewModel => null;
        public PageKey CurrentPage => Navigated;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Navigate(PageKey page) { Navigated = page; NavigateCount++; PropertyChanged?.Invoke(this, new(nameof(CurrentPage))); }
    }

    private sealed class Harness
    {
        public FakeTray Tray = new();
        public FakeWindow Window = new();
        public FakeSession Session = new();
        public FakeLauncher Launcher = new();
        public FakeNav Nav = new();
        public LocalizationService Loc = new();
        public bool MinimizeToTray = true;
        public bool CloseToTray;
        public bool ConfirmExit = true;
        public int ShutdownCount;
        public SystemTrayService Service = null!;

        public SystemTrayService Build()
        {
            Service = new SystemTrayService(
                Tray, Window, Session, Nav, Launcher, Loc,
                minimizeToTray: () => MinimizeToTray,
                closeToTray: () => CloseToTray,
                confirmExitWhileRunning: () => ConfirmExit,
                shutdown: () => ShutdownCount++,
                NullLogger<SystemTrayService>.Instance);
            Service.Start();
            return Service;
        }
    }

    // ---- minimize --------------------------------------------------------

    [Fact] // scenario 1: MinimizeToTray on → hides to tray
    public void Minimize_On_Hides()
    {
        var h = new Harness { MinimizeToTray = true }; h.Build();
        Assert.True(h.Service.HandleMinimize());
        Assert.Equal(1, h.Window.HideCount);
    }

    [Fact] // scenario 2: MinimizeToTray off → standard minimize (no hide)
    public void Minimize_Off_KeepsTaskbar()
    {
        var h = new Harness { MinimizeToTray = false }; h.Build();
        Assert.False(h.Service.HandleMinimize());
        Assert.Equal(0, h.Window.HideCount);
    }

    [Fact] // scenario 3: hiding to tray does not stop a running session
    public void Minimize_DoesNotStopSession()
    {
        var h = new Harness { MinimizeToTray = true }; h.Session.IsRunning = true; h.Session.State = SessionState.Running; h.Build();
        h.Service.HandleMinimize();
        Assert.Equal(0, h.Session.StopCount);
    }

    [Fact] // the "still running in tray" hint appears once, not on every minimize
    public void Minimize_HintShownOnce()
    {
        var h = new Harness { MinimizeToTray = true }; h.Build();
        h.Service.HandleMinimize();
        h.Service.HandleMinimize();
        h.Service.HandleMinimize();
        Assert.Equal(1, h.Tray.Balloons);
    }

    // ---- double click / restore -----------------------------------------

    [Fact] // scenario 5: double-click restores the window
    public void DoubleClick_Restores()
    {
        var h = new Harness(); h.Build();
        h.Tray.RaiseDoubleClick();
        Assert.Equal(1, h.Window.RestoreCount);
    }

    // ---- close behaviour -------------------------------------------------

    [Fact] // scenario 7: CloseToTray on → X hides, does not exit
    public async Task Close_CloseToTray_Hides()
    {
        var h = new Harness { CloseToTray = true }; h.Build();
        await h.Service.HandleWindowCloseAsync();
        Assert.Equal(1, h.Window.HideCount);
        Assert.Equal(0, h.ShutdownCount);
        Assert.False(h.Service.IsExiting);
    }

    [Fact] // scenario 8: CloseToTray off, idle → X exits
    public async Task Close_NoCloseToTray_Idle_Exits()
    {
        var h = new Harness { CloseToTray = false }; h.Build();
        await h.Service.HandleWindowCloseAsync();
        Assert.Equal(1, h.ShutdownCount);
        Assert.True(h.Service.IsExiting);
    }

    [Fact] // scenario 9: tray "Exit" is never intercepted by CloseToTray
    public async Task Exit_NotInterceptedByCloseToTray()
    {
        var h = new Harness { CloseToTray = true }; h.Build();
        await h.Service.RequestExitAsync();
        Assert.Equal(1, h.ShutdownCount);
        Assert.Equal(1, h.Tray.DisposeCount); // icon released before shutdown
    }

    // ---- exit confirmation ----------------------------------------------

    [Fact] // scenario 26: exit while running, user cancels → session continues, no shutdown
    public async Task Exit_Running_Cancel_Continues()
    {
        var h = new Harness { ConfirmExit = false }; h.Session.IsRunning = true; h.Session.State = SessionState.Running; h.Build();
        await h.Service.RequestExitAsync();
        Assert.Equal(0, h.Session.StopCount);
        Assert.Equal(0, h.ShutdownCount);
        Assert.False(h.Service.IsExiting);
    }

    [Fact] // scenario 27/28: exit while running, confirmed → safe stop, then shutdown + icon released
    public async Task Exit_Running_Confirm_StopsThenShuts()
    {
        var h = new Harness { ConfirmExit = true }; h.Session.IsRunning = true; h.Session.State = SessionState.Running; h.Session.CanStop = true; h.Build();
        await h.Service.RequestExitAsync();
        Assert.Equal(1, h.Session.StopCount);
        Assert.True(h.Service.IsExiting);
        Assert.Equal(1, h.ShutdownCount);
        Assert.Equal(1, h.Tray.DisposeCount);
    }

    // ---- commands --------------------------------------------------------

    [Fact] // scenario 13: Start session restores the window and opens the shared launcher (dialog)
    public void StartCommand_RestoresAndLaunches()
    {
        var h = new Harness(); h.Build();
        h.Tray.RaiseCommand(TrayCommand.StartSession);
        Assert.Equal(1, h.Window.RestoreCount);
        Assert.Equal(1, h.Launcher.StartCount);
    }

    [Fact] // scenario 15: Stop reuses the existing StopAsync
    public void StopCommand_ReusesStop()
    {
        var h = new Harness(); h.Session.State = SessionState.Running; h.Session.CanStop = true; h.Build();
        h.Tray.RaiseCommand(TrayCommand.StopSession);
        Assert.Equal(1, h.Session.StopCount);
    }

    [Fact] // scenario 16: overlay toggle flips the shared state and the menu label follows
    public void ToggleOverlay_SyncsMenu()
    {
        var h = new Harness(); h.Session.OverlayVisible = false; h.Build();
        Assert.Equal("显示字幕浮窗", h.Tray.Item(TrayCommand.ToggleOverlay).Text);

        h.Tray.RaiseCommand(TrayCommand.ToggleOverlay);
        Assert.Equal(1, h.Session.ToggleCount);
        Assert.Equal("隐藏字幕浮窗", h.Tray.Item(TrayCommand.ToggleOverlay).Text); // rebuilt after Changed
    }

    [Fact] // scenario 17: Open settings restores the window and navigates to Settings
    public void OpenSettings_RestoresAndNavigates()
    {
        var h = new Harness(); h.Build();
        h.Tray.RaiseCommand(TrayCommand.OpenSettings);
        Assert.Equal(1, h.Window.RestoreCount);
        Assert.Equal(PageKey.Settings, h.Nav.Navigated);
    }

    [Fact] // scenario 18: Open main window restores without forcing navigation
    public void OpenMainWindow_RestoresNoNav()
    {
        var h = new Harness(); h.Build();
        h.Tray.RaiseCommand(TrayCommand.OpenMainWindow);
        Assert.Equal(1, h.Window.RestoreCount);
        Assert.Equal(0, h.Nav.NavigateCount);
    }

    // ---- state / language / lifecycle -----------------------------------

    [Fact] // menu tracks live state changes (running → stop enabled)
    public void Menu_TracksStateChange()
    {
        var h = new Harness(); h.Build();
        Assert.True(h.Tray.Item(TrayCommand.StartSession).Enabled);

        h.Session.State = SessionState.Running;
        h.Session.Raise();
        Assert.True(h.Tray.Item(TrayCommand.StopSession).Enabled);
        Assert.False(h.Tray.Item(TrayCommand.StartSession).Enabled);
    }

    [Fact] // scenario 21: switching UI language rebuilds the menu immediately (no restart)
    public void LanguageSwitch_RebuildsMenu()
    {
        var h = new Harness(); h.Build();
        Assert.Equal("开始会话", h.Tray.Item(TrayCommand.StartSession).Text);
        h.Loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal("Start session", h.Tray.Item(TrayCommand.StartSession).Text);
    }

    [Fact] // scenario 24/25/28: Dispose is idempotent and releases the icon
    public void Dispose_IsIdempotent()
    {
        var h = new Harness(); h.Build();
        h.Service.Dispose();
        h.Service.Dispose();
        Assert.Equal(1, h.Tray.DisposeCount);
    }

    [Fact] // scenario 22: Start is idempotent (one icon; a second Start does not re-subscribe)
    public void Start_IsIdempotent()
    {
        var h = new Harness(); h.Build();
        h.Service.Start(); // second call — must be a no-op
        h.Tray.RaiseDoubleClick();
        Assert.Equal(1, h.Window.RestoreCount); // not doubled by a second subscription
    }
}
