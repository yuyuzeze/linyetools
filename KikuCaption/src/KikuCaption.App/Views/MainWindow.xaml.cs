using System.IO;
using System.Windows;
using KikuCaption.App.ViewModels;

namespace KikuCaption.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SubtitleOverlayWindow _overlay;
    private bool _safeStopInProgress;

    public MainWindow(MainViewModel viewModel, SubtitleOverlayWindow overlay)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _overlay = overlay;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    // Milestone 7 §4: closing while recording → confirm, then a safe stop (never a hard kill).
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_safeStopInProgress || !_viewModel.Realtime.IsRunning)
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
            if (_viewModel.Realtime.StopCommand.CanExecute(null))
            {
                await _viewModel.Realtime.StopCommand.ExecuteAsync(null);
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
        if (_viewModel.CheckEnvironmentCommand.CanExecute(null))
        {
            await _viewModel.CheckEnvironmentCommand.ExecuteAsync(null);
        }

        // Crash recovery for any session left incomplete by a previous run (Milestone 4).
        await _viewModel.RunRecoveryAsync();
    }

    // The view only picks the output file (a WPF dialog, not audio logic) and delegates
    // the actual capture to the view model / recorder.
    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        var audio = _viewModel.Audio;
        var suggested = audio.SuggestDefaultOutputPath();
        var initialDirectory = Path.GetDirectoryName(suggested);
        if (!string.IsNullOrEmpty(initialDirectory))
        {
            Directory.CreateDirectory(initialDirectory);
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存系统音频 WAV",
            Filter = "WAV 音频 (*.wav)|*.wav",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = initialDirectory,
            AddExtension = true,
            DefaultExt = ".wav",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await audio.StartAsync(dialog.FileName);
        }
    }

    // The API key is read from the PasswordBox and handed straight to the DPAPI secret store; it is
    // never bound, echoed, or logged (PROJECT.md 5.6, M6 §8).
    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Translation.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private async void RecognizeWav_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要识别的 WAV 文件",
            Filter = "WAV 音频 (*.wav)|*.wav",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.Speech.RecognizeWavAsync(dialog.FileName);
        }
    }
}
