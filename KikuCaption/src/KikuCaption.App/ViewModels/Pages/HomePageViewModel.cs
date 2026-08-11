using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.Storage;
using KikuCaption.Storage.Recovery;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Home page view model — the slim everyday meeting surface (UI-R2). It keeps only what a meeting
/// needs: recognition language, the translation quick control (toggle + direction), start/stop, the
/// overlay toggle, a compact current-session status card, and the full meeting timeline. The audio
/// capture and WAV-recognition test tools moved to the Audio page; translation configuration moved
/// to the Settings page. The meeting pipeline (RealtimeCaptionViewModel) is unchanged.
/// </summary>
public partial class HomePageViewModel : ObservableObject
{
    private readonly SessionRecoveryService _recoveryService;
    private readonly StorageOptions _storage;
    private readonly ILogger<HomePageViewModel> _logger;
    private readonly DispatcherTimer _elapsedTimer;
    private DateTime _sessionStartedUtc;

    public HomePageViewModel(
        RealtimeCaptionViewModel realtime,
        TranslationViewModel translation,
        SessionRecoveryService recoveryService,
        StorageOptions storage,
        ILogger<HomePageViewModel> logger)
    {
        Realtime = realtime;
        Translation = translation;
        _recoveryService = recoveryService;
        _storage = storage;
        _logger = logger;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        Realtime.PropertyChanged += OnRealtimeChanged;
        Realtime.Timeline.PropertyChanged += OnTimelineChanged;
        Translation.PropertyChanged += OnTranslationChanged;
    }

    /// <summary>Real-time captioning + overlay controls and meeting timeline (unchanged pipeline).</summary>
    public RealtimeCaptionViewModel Realtime { get; }

    /// <summary>Translation quick control (toggle + direction). Full config lives on the Settings page.</summary>
    public TranslationViewModel Translation { get; }

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _recoveryStatus = string.Empty;

    /// <summary>True when there is no running session and no captions yet — drives the empty state.</summary>
    public bool HasNoSession => !Realtime.IsRunning && Realtime.Timeline.FinalCount == 0;

    /// <summary>Where meetings are written (shown as a summary in the start dialog and status card).</summary>
    public string OutputRootSummary => _storage.ResolveOutputRoot();

    /// <summary>Guide the user to configure translation when they enable it without a valid config.</summary>
    public bool TranslationNotConfiguredHint => Translation.Enabled && !Translation.IsConfigured;

    /// <summary>Runs crash recovery on startup: rebuilds files for any never-completed session.</summary>
    public async Task RunRecoveryAsync()
    {
        try
        {
            var result = await _recoveryService.RecoverAsync(CancellationToken.None).ConfigureAwait(true);
            RecoveryStatus = result.RecoveredCount == 0 && result.FailedCount == 0
                ? string.Empty
                : $"启动恢复：已恢复 {result.RecoveredCount} 个会话" +
                  (result.FailedCount > 0 ? $"，{result.FailedCount} 个失败（详见日志）。" : "。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup recovery failed.");
            RecoveryStatus = "启动恢复失败（数据库可能损坏）：" + ex.Message;
        }
    }

    private void OnRealtimeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RealtimeCaptionViewModel.IsRunning))
        {
            if (Realtime.IsRunning)
            {
                _sessionStartedUtc = DateTime.UtcNow;
                UpdateElapsed();
                _elapsedTimer.Start();
            }
            else
            {
                _elapsedTimer.Stop();
            }

            OnPropertyChanged(nameof(HasNoSession));
        }
    }

    private void OnTimelineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingTimelineViewModel.FinalCount))
        {
            OnPropertyChanged(nameof(HasNoSession));
        }
    }

    private void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslationViewModel.Enabled)
            or nameof(TranslationViewModel.IsConfigured)
            or nameof(TranslationViewModel.DirectionText))
        {
            OnPropertyChanged(nameof(TranslationNotConfiguredHint));
        }
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTime.UtcNow - _sessionStartedUtc;
        ElapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }
}
