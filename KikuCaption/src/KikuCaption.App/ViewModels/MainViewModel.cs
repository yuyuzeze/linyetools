using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.Core.Interfaces;
using KikuCaption.Storage.Recovery;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Main-window view model. Runs the environment check off the UI thread and exposes the
/// results. All work is asynchronous so the UI never blocks (PROJECT.md 14.2, M0).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IEnvironmentChecker _environmentChecker;
    private readonly SessionRecoveryService _recoveryService;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _overallMessage = "尚未检查运行环境。";

    [ObservableProperty]
    private bool _hasBlockingIssues;

    public MainViewModel(
        IEnvironmentChecker environmentChecker,
        AudioCaptureViewModel audio,
        SpeechViewModel speech,
        RealtimeCaptionViewModel realtime,
        TranslationViewModel translation,
        SessionRecoveryService recoveryService,
        ILogger<MainViewModel> logger)
    {
        _environmentChecker = environmentChecker;
        Audio = audio;
        Speech = speech;
        Realtime = realtime;
        Translation = translation;
        _recoveryService = recoveryService;
        _logger = logger;
    }

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

    /// <summary>Milestone 1 system-audio capture (WAV validation) panel.</summary>
    public AudioCaptureViewModel Audio { get; }

    /// <summary>Milestone 2 speech recognition (WAV) panel.</summary>
    public SpeechViewModel Speech { get; }

    /// <summary>Milestone 3 real-time captioning + overlay controls.</summary>
    public RealtimeCaptionViewModel Realtime { get; }

    /// <summary>Milestone 6 translation settings + status.</summary>
    public TranslationViewModel Translation { get; }

    public ObservableCollection<EnvironmentItemViewModel> Items { get; } = new();

    [RelayCommand]
    private async Task CheckEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        OverallMessage = "正在检查运行环境……";
        Items.Clear();

        try
        {
            var report = await _environmentChecker.CheckAsync(cancellationToken).ConfigureAwait(true);

            foreach (var result in report.Results)
            {
                Items.Add(new EnvironmentItemViewModel(result));
            }

            HasBlockingIssues = report.HasBlockingIssues;
            OverallMessage = report.HasBlockingIssues
                ? "部分必需依赖缺失或不可用（详见下方）。程序不会崩溃，但相关功能需在后续里程碑安装依赖后才能使用。"
                : "运行环境检查完成，未发现阻断性问题。";
        }
        catch (OperationCanceledException)
        {
            OverallMessage = "环境检查已取消。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment check failed unexpectedly");
            OverallMessage = "环境检查过程出错，详见日志。";
        }
        finally
        {
            IsChecking = false;
        }
    }
}
