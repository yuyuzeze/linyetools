using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.Storage.Recovery;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Home page view model — the everyday meeting surface. It aggregates the live meeting sub-view
/// models (audio, speech, real-time captioning + timeline, translation). UI-R1 migrates the
/// existing home content here unchanged (the meeting pipeline is untouched) and strips the
/// developer/Milestone framing; UI-R2 will slim the home layout and move the audio/speech test
/// panels to the Audio page.
/// </summary>
public partial class HomePageViewModel : ObservableObject
{
    private readonly SessionRecoveryService _recoveryService;
    private readonly ILogger<HomePageViewModel> _logger;

    public HomePageViewModel(
        AudioCaptureViewModel audio,
        SpeechViewModel speech,
        RealtimeCaptionViewModel realtime,
        TranslationViewModel translation,
        SessionRecoveryService recoveryService,
        ILogger<HomePageViewModel> logger)
    {
        Audio = audio;
        Speech = speech;
        Realtime = realtime;
        Translation = translation;
        _recoveryService = recoveryService;
        _logger = logger;
    }

    /// <summary>System-audio capture (WAV) panel.</summary>
    public AudioCaptureViewModel Audio { get; }

    /// <summary>Local speech recognition (WAV) panel.</summary>
    public SpeechViewModel Speech { get; }

    /// <summary>Real-time captioning + overlay controls and meeting timeline.</summary>
    public RealtimeCaptionViewModel Realtime { get; }

    /// <summary>Translation settings + status.</summary>
    public TranslationViewModel Translation { get; }

    [ObservableProperty]
    private string _recoveryStatus = string.Empty;

    /// <summary>Runs crash recovery on startup: rebuilds files for any never-completed session.</summary>
    public async Task RunRecoveryAsync()
    {
        try
        {
            var result = await _recoveryService.RecoverAsync(CancellationToken.None).ConfigureAwait(true);
            RecoveryStatus = result.RecoveredCount == 0 && result.FailedCount == 0
                ? "启动恢复检查：无需恢复的会话。"
                : $"启动恢复：已恢复 {result.RecoveredCount} 个会话" +
                  (result.FailedCount > 0 ? $"，{result.FailedCount} 个失败（详见日志）。" : "。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup recovery failed.");
            RecoveryStatus = "启动恢复失败（数据库可能损坏）：" + ex.Message;
        }
    }
}
