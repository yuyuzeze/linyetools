using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.Audio.Wav;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Milestone 2 verification panel: pick a WAV, choose ja/zh, and recognize it via the resident
/// Python worker. All work is async so the UI never blocks on model load or inference.
/// </summary>
public partial class SpeechViewModel : ObservableObject
{
    private readonly Func<ISpeechRecognizer> _recognizerFactory;
    private readonly ISpeechOptionsProvider _speechOptionsProvider;
    private readonly ILogger<SpeechViewModel> _logger;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _selectedLanguage = "ja";

    [ObservableProperty]
    private string _statusText = "选择识别语言，然后识别一个 WAV 文件。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public SpeechViewModel(
        Func<ISpeechRecognizer> recognizerFactory,
        ISpeechOptionsProvider speechOptionsProvider,
        ILogger<SpeechViewModel> logger)
    {
        _recognizerFactory = recognizerFactory;
        _speechOptionsProvider = speechOptionsProvider;
        _logger = logger;
    }

    public IReadOnlyList<string> Languages { get; } = new[] { "ja", "zh" };

    public ObservableCollection<string> Results { get; } = new();

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task RecognizeWavAsync(string wavPath)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Results.Clear();
        StatusText = "正在启动 Worker 并加载模型（首次约 1–2 秒，未缓存模型时更久）……";

        try
        {
            await using var recognizer = _recognizerFactory();
            // Same full, per-language config as the real-time pipeline (single source of truth).
            await recognizer.InitializeAsync(_speechOptionsProvider.ForLanguage(SelectedLanguage), CancellationToken.None);
            StatusText = "模型已就绪，正在识别……";

            await foreach (var update in recognizer.RecognizeAsync(WavFileAudioReader.ReadAsync(wavPath), CancellationToken.None))
            {
                if (update.Kind == TranscriptUpdateKind.FinalCandidate)
                {
                    Results.Add($"[{Format(update.StartTime)}–{Format(update.EndTime)}] {update.Text}");
                }
            }

            StatusText = $"识别完成，共 {Results.Count} 段（语言：{SelectedLanguage}）。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAV recognition failed.");
            ErrorMessage = "识别失败：" + ex.Message;
            StatusText = "识别失败，详见提示与日志。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Format(TimeSpan time) => time.ToString(@"mm\:ss\.ff");
}
