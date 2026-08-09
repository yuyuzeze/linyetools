using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.Audio.Capture;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// View model for the Milestone 1 WAV capture panel. Delegates all capture/conversion work
/// to <see cref="ISystemAudioWavRecorder"/> (no WASAPI here) and keeps the UI responsive:
/// a <see cref="DispatcherTimer"/> refreshes the status text on the UI thread.
/// </summary>
public partial class AudioCaptureViewModel : ObservableObject
{
    private const int BytesPerSecond = 16000 * 2; // 16 kHz * 16-bit mono

    private readonly ISystemAudioWavRecorder _recorder;
    private readonly IOptions<KikuCaptionOptions> _options;
    private readonly ILogger<AudioCaptureViewModel> _logger;
    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _statusText = "未开始。选择输出文件后点击“开始捕获”。";

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public AudioCaptureViewModel(
        ISystemAudioWavRecorder recorder,
        IOptions<KikuCaptionOptions> options,
        ILogger<AudioCaptureViewModel> logger)
    {
        _recorder = recorder;
        _options = options;
        _logger = logger;

        _recorder.Faulted += OnRecorderFaulted;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateLiveStatus();
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Suggests a unique, non-overwriting default WAV path under the output directory.</summary>
    public string SuggestDefaultOutputPath()
    {
        var configured = _options.Value.Storage.OutputDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        var directory = Path.Combine(root, "_audio_tests");
        var fileName = $"system-audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        return Path.Combine(directory, fileName);
    }

    public async Task StartAsync(string outputFilePath)
    {
        ErrorMessage = null;
        try
        {
            await _recorder.StartAsync(outputFilePath);
            OutputPath = _recorder.OutputPath;
            IsCapturing = true;
            StatusText = "捕获中……";
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WAV capture.");
            ErrorMessage = "开始捕获失败：" + ex.Message;
            IsCapturing = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        _statusTimer.Stop();
        try
        {
            await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping WAV capture.");
            ErrorMessage = "停止时出错：" + ex.Message;
        }

        IsCapturing = false;
        var seconds = _recorder.BytesWritten / (double)BytesPerSecond;
        StatusText = _recorder.State == AudioRecorderState.Faulted
            ? StatusText
            : $"已停止。已保存约 {seconds:0.0}s 音频到：{_recorder.OutputPath}";
    }

    private void UpdateLiveStatus()
    {
        var seconds = _recorder.BytesWritten / (double)BytesPerSecond;
        StatusText =
            $"捕获中…… 已用时 {_recorder.Elapsed:mm\\:ss}，已写入约 {seconds:0.0}s 音频（{_recorder.BytesWritten / 1024} KB）。";
    }

    private void OnRecorderFaulted(object? sender, AudioRecorderFaultedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            _statusTimer.Stop();
            IsCapturing = false;
            ErrorMessage = "捕获中断：" + e.Message;
            StatusText = "已因错误停止。原始已写入的数据仍保留在 WAV 文件中。";
        });
    }
}
