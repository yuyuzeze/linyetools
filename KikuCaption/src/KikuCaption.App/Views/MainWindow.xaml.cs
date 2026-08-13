using System.Windows;
using KikuCaption.App.Tray;
using KikuCaption.App.ViewModels;
using KikuCaption.Infrastructure.Configuration;

namespace KikuCaption.App.Views;

/// <summary>
/// Main-window shell. Code-behind is limited to unavoidable window/view behaviours: the tray
/// minimize/close hooks (UI-R5B), closing the overlay, wiring startup initialization, and closing the
/// environment dropdown. It implements <see cref="IMainWindowController"/> so the tray coordinator can
/// hide/restore it; all session/exit decisions live in <see cref="ISystemTrayService"/>.
/// </summary>
public partial class MainWindow : Window, IMainWindowController
{
    private readonly ShellViewModel _shell;
    private readonly SubtitleOverlayWindow _overlay;
    private ISystemTrayService? _tray;
    private bool _legacyStopInProgress;
    private readonly UserSettingsStore _settingsStore;
    private int _brandClickCount;
    private DateTimeOffset _lastBrandClick;

    public MainWindow(ShellViewModel shell, SubtitleOverlayWindow overlay, UserSettingsStore settingsStore)
    {
        InitializeComponent();
        _shell = shell;
        _overlay = overlay;
        _settingsStore = settingsStore;
        DataContext = shell;

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Wires the tray coordinator after construction (avoids a DI ctor cycle).</summary>
    public void AttachTray(ISystemTrayService tray) => _tray = tray;

    // ---- IMainWindowController (called by the tray, on the UI thread) ----

    public void HideToTray()
    {
        Hide();
        ShowInTaskbar = false; // gone from the taskbar; the session keeps running
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal; // back to the pre-minimize size/position
        }

        Activate();
        // Briefly toggle Topmost to pull the window to the foreground, then release it — the window is
        // never left permanently topmost, and the subtitle overlay's Topmost is untouched.
        Topmost = true;
        Topmost = false;
    }

    // ---- window events ---------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _tray?.HandleMinimize(); // hides to tray when MinimizeToTray is on; standard minimize otherwise
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_tray is null)
        {
            await LegacyCloseAsync(e); // defensive fallback if the tray was never attached
            return;
        }

        if (_tray.IsExiting)
        {
            return; // a real exit is underway (Application.Shutdown) → allow this close
        }

        // The tray decides: hide-to-tray (CloseToTray) or the real-exit flow (confirm-if-running →
        // safe stop → ordered shutdown). Hold the close until it has decided.
        e.Cancel = true;
        await _tray.HandleWindowCloseAsync();
    }

    // Pre-R5B behaviour, kept only for the (production-unreachable) no-tray path.
    private async Task LegacyCloseAsync(System.ComponentModel.CancelEventArgs e)
    {
        var realtime = _shell.Home.Realtime;
        if (_legacyStopInProgress || !realtime.IsRunning)
        {
            Application.Current?.Shutdown();
            return;
        }

        e.Cancel = true;
        var loc = KikuCaption.App.Localization.LocalizationService.Instance;
        var choice = MessageBox.Show(loc["Confirm.CloseWhileRecording"], loc["Common.AppName"],
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        _legacyStopInProgress = true;
        try
        {
            if (realtime.StopCommand.CanExecute(null))
            {
                await realtime.StopCommand.ExecuteAsync(null);
            }
        }
        catch { /* stop is best-effort; data is already persisted */ }

        Application.Current?.Shutdown();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Closing the main window must close the overlay too (PROJECT.md M3).
        try { _overlay.Close(); } catch { /* ignore */ }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // Show Home, then run the environment check + crash recovery off the UI thread.
        await _shell.InitializeAsync();
    }

    // Close the environment dropdown once one of its items is chosen (the command still runs).
    private void EnvMenuItem_Click(object sender, RoutedEventArgs e) => EnvMenuToggle.IsChecked = false;

    private void Brand_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        _brandClickCount = now - _lastBrandClick <= TimeSpan.FromSeconds(2) ? _brandClickCount + 1 : 1;
        _lastBrandClick = now;
        if (_brandClickCount < 5) return;
        _brandClickCount = 0;

        var (settings, _) = _settingsStore.Load();
        var nextTheme = SubtitleThemeCycle.Next(settings.SubtitleTheme);
        _settingsStore.Save(settings with { SubtitleTheme = nextTheme });
        _shell.Home.Realtime.Overlay.ApplyTheme(nextTheme);

        var loc = KikuCaption.App.Localization.LocalizationService.Instance;
        MessageBox.Show(loc[$"EasterEgg.Theme.{nextTheme}"], loc["EasterEgg.Title"],
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
