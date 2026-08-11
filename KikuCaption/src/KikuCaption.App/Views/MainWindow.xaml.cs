using System.Windows;
using KikuCaption.App.ViewModels;
using KikuCaption.App.Views.Pages;

namespace KikuCaption.App.Views;

/// <summary>
/// Main-window shell. Code-behind is limited to unavoidable window/view behaviours: the
/// close-while-recording safe-stop guard, closing the overlay, wiring the startup initialization,
/// and closing the environment dropdown after a menu click. All feature logic lives in view models.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly SubtitleOverlayWindow _overlay;
    private bool _safeStopInProgress;

    public MainWindow(ShellViewModel shell, SubtitleOverlayWindow overlay)
    {
        InitializeComponent();
        _shell = shell;
        _overlay = overlay;
        DataContext = shell;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    // Closing while recording → confirm, then a safe stop (never a hard kill).
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var realtime = _shell.Home.Realtime;
        if (_safeStopInProgress || !realtime.IsRunning)
        {
            return;
        }

        e.Cancel = true; // hold the close until the session stops safely
        var choice = MessageBox.Show(
            "会议正在录制中。关闭前将安全停止并保存 MP4 与字幕（不会强制结束进程）。是否继续关闭？",
            "KikuCaption", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        _safeStopInProgress = true;
        try
        {
            if (realtime.StopCommand.CanExecute(null))
            {
                await realtime.StopCommand.ExecuteAsync(null);
            }
        }
        catch { /* stop is best-effort; data is already persisted */ }

        Close(); // now allowed (IsRunning is false)
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
}
