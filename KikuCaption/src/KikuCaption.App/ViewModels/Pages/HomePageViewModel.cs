using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.App.Playback;
using KikuCaption.App.Services;
using KikuCaption.App.ViewModels;
using KikuCaption.Audio.Capture;
using KikuCaption.Audio.Diagnostics;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Storage;
using KikuCaption.Storage.Recovery;
using KikuCaption.Storage.Sqlite;
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
    private readonly UserSettingsStore _settingsStore;
    private readonly LocalizationService _loc;
    private readonly IAudioDeviceInfoProvider _devices;
    private readonly Func<MicrophoneLevelMeter> _meterFactory;
    private readonly Func<IMeetingLauncher> _launcherFactory;
    private readonly KikuCaption.App.Services.MeetingSummaryCoordinator _summary;
    private readonly KikuCaption.Translation.TranslationOptions _translationOptions;
    private readonly KikuCaption.Summarization.MeetingSummaryOptions _summaryOptions;
    private readonly ILogger<HomePageViewModel> _logger;
    private readonly ITranscriptStore _store;
    private readonly MeetingPlaybackWindowManager _playback;
    private readonly MeetingSessionDeletionService _deletion;
    private readonly DispatcherTimer _elapsedTimer;
    private DateTime _sessionStartedUtc;

    public HomePageViewModel(
        RealtimeCaptionViewModel realtime,
        TranslationViewModel translation,
        SessionRecoveryService recoveryService,
        StorageOptions storage,
        UserSettingsStore settingsStore,
        LocalizationService localization,
        IAudioDeviceInfoProvider devices,
        Func<MicrophoneLevelMeter> meterFactory,
        Func<IMeetingLauncher> launcherFactory,
        KikuCaption.App.Services.MeetingSummaryCoordinator summary,
        KikuCaption.Translation.TranslationOptions translationOptions,
        KikuCaption.Summarization.MeetingSummaryOptions summaryOptions,
        ITranscriptStore store,
        MeetingPlaybackWindowManager playback,
        MeetingSessionDeletionService deletion,
        ILogger<HomePageViewModel> logger)
    {
        Realtime = realtime;
        Translation = translation;
        _recoveryService = recoveryService;
        _storage = storage;
        _settingsStore = settingsStore;
        _loc = localization;
        _devices = devices;
        _meterFactory = meterFactory;
        _launcherFactory = launcherFactory;
        _summary = summary;
        _translationOptions = translationOptions;
        _summaryOptions = summaryOptions;
        _store = store;
        _playback = playback;
        _deletion = deletion;
        _logger = logger;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();

        Realtime.PropertyChanged += OnRealtimeChanged;
        Realtime.Timeline.PropertyChanged += OnTimelineChanged;
        Translation.PropertyChanged += OnTranslationChanged;
        _loc.LanguageChanged += (_, _) =>
        {
            RaiseTranslationDisplay();
            foreach (var meeting in RecentMeetings) meeting.RefreshLocalization();
        };
    }

    // ---- UI-R4A translation direction display (source follows recognition; target from session
    // snapshot while running, else live setting). Localized; recomputed on the relevant changes. ----

    private string EffectiveSource => Realtime.SelectedLanguage;

    private string EffectiveTarget => Realtime.IsRunning
        ? (Realtime.SessionTargetLanguage ?? Translation.TargetLanguage)
        : Translation.TargetLanguage;

    /// <summary>Localized "source → target", e.g. 日本語 → 中文 / Japanese → Chinese / 日本語 → 中国語.</summary>
    public string TranslationDirectionText => $"{_loc["Lang." + EffectiveSource]} → {_loc["Lang." + EffectiveTarget]}";

    /// <summary>True when the effective source and target are the same language (no translation needed).</summary>
    public bool IsTranslationSameLanguage => string.Equals(EffectiveSource, EffectiveTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>Localized state prefix: "翻译中：" while running, "无需翻译：" when same-language, else empty.</summary>
    public string TranslationPrefix
    {
        get
        {
            if (IsTranslationSameLanguage)
            {
                return _loc["Home.NoTranslationNeeded"];
            }

            return Realtime.IsRunning && Translation.Enabled ? _loc["Home.Translating"] : string.Empty;
        }
    }

    /// <summary>Dim the direction when translation is off or same-language (UI-R4A §8).</summary>
    public bool TranslationDimmed => !Translation.Enabled || IsTranslationSameLanguage;

    private void RaiseTranslationDisplay()
    {
        OnPropertyChanged(nameof(TranslationDirectionText));
        OnPropertyChanged(nameof(IsTranslationSameLanguage));
        OnPropertyChanged(nameof(TranslationPrefix));
        OnPropertyChanged(nameof(TranslationDimmed));
    }

    /// <summary>Real-time captioning + overlay controls and meeting timeline (unchanged pipeline).</summary>
    public RealtimeCaptionViewModel Realtime { get; }

    /// <summary>Translation quick control (toggle + direction). Full config lives on the Settings page.</summary>
    public TranslationViewModel Translation { get; }

    public ObservableCollection<RecentMeetingViewModel> RecentMeetings { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRecentMeetings))]
    private bool _isLoadingRecentMeetings;

    [ObservableProperty]
    private string _recentMeetingsError = string.Empty;

    public bool HasNoRecentMeetings => !IsLoadingRecentMeetings && RecentMeetings.Count == 0;

    public async Task LoadRecentMeetingsAsync()
    {
        if (IsLoadingRecentMeetings) return;
        IsLoadingRecentMeetings = true;
        RecentMeetingsError = string.Empty;
        try
        {
            var sessions = await _store.GetRecentSessionsAsync(20, CancellationToken.None).ConfigureAwait(true);
            RecentMeetings.Clear();
            foreach (var session in sessions)
            {
                RecentMeetings.Add(new RecentMeetingViewModel(
                    session, _loc, _summary.SummaryExists(session.Session.OutputDirectory)));
            }
            OnPropertyChanged(nameof(HasNoRecentMeetings));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading recent meetings failed.");
            RecentMeetingsError = _loc["History.LoadFailed"];
        }
        finally
        {
            IsLoadingRecentMeetings = false;
            OnPropertyChanged(nameof(HasNoRecentMeetings));
        }
    }

    [RelayCommand]
    private async Task RefreshRecentMeetingsAsync() => await LoadRecentMeetingsAsync();

    [RelayCommand(CanExecute = nameof(CanLoadHistory))]
    private async Task LoadHistoryAsync(RecentMeetingViewModel? meeting)
    {
        if (meeting is null) return;
        await Realtime.Timeline.LoadHistoryAsync(meeting.SessionId, CancellationToken.None).ConfigureAwait(true);
    }

    private bool CanLoadHistory(RecentMeetingViewModel? meeting) => meeting is not null && !Realtime.IsRunning;

    public bool CanOpenDisplayedPlayback
        => Realtime.Timeline.DisplayedSession is { RecordingPath: not null } && !Realtime.IsRunning;

    public Task<string?> OpenPlaybackAsync(Guid sessionId, Window? owner)
        => _playback.OpenAsync(sessionId, owner, CancellationToken.None);

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

    /// <summary>
    /// Persists the confirmed capture target so the choice is remembered across restarts (UI-R3).
    /// Called after a valid start-meeting confirm; failures are logged, never surfaced as a crash.
    /// </summary>
    public void PersistCaptureTarget(MeetingCaptureTarget target)
    {
        try
        {
            SettingsPersistence.PersistCaptureTarget(_settingsStore, target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting capture target failed.");
        }
    }

    /// <summary>Opens the start-meeting dialog and starts on confirm (UI-R5B: shared with the tray).</summary>
    public Task StartMeetingAsync() => _launcherFactory().StartFromDialogAsync();

    // ---- UI-R5C meeting summary (current session) -----------------------

    /// <summary>True when the displayed (stopped/history) session has final captions and is not busy.</summary>
    public bool CanGenerateSummary
        => BuildDisplayedContext() is { } c && KikuCaption.App.Services.MeetingSummaryCoordinator.CanGenerate(c.State, c.FinalCount);

    /// <summary>True when a meeting-summary.md already exists for the displayed session.</summary>
    public bool SummaryFileExists
        => BuildDisplayedContext() is { } c && _summary.SummaryExists(c.SessionDirectory);

    public bool CanDeleteDisplayedSession
        => !Realtime.IsRunning && Realtime.Timeline.DisplayedSession is not null;

    public async Task DeleteDisplayedSessionAsync()
    {
        var displayed = Realtime.Timeline.DisplayedSession;
        if (displayed is null || Realtime.IsRunning)
        {
            return;
        }

        _playback.Close(displayed.SessionId);
        await _deletion.DeleteAsync(displayed.SessionId, CancellationToken.None).ConfigureAwait(true);
        Realtime.Timeline.ClearDisplayCommand.Execute(null);
        await LoadRecentMeetingsAsync().ConfigureAwait(true);
        RaiseSummaryState();
        OnPropertyChanged(nameof(CanDeleteDisplayedSession));
    }

    // The summary always targets the session the timeline is CURRENTLY showing — its real id and
    // directory, from the actual record. A live session uses its live state; a loaded history session
    // is idle regardless of any other running meeting, so we never mis-gate on a global "is running".
    private KikuCaption.App.Services.SummarySessionContext? BuildDisplayedContext()
    {
        var d = Realtime.Timeline.DisplayedSession;
        if (d is null || string.IsNullOrWhiteSpace(d.Directory) || Realtime.Timeline.FinalCount == 0)
        {
            return null;
        }

        var state = d.IsLive ? Realtime.CurrentSessionState : Core.Enums.SessionState.Idle;
        return new KikuCaption.App.Services.SummarySessionContext(
            d.SessionId, d.Directory, d.Language, d.Date, state, Realtime.Timeline.FinalCount);
    }

    /// <summary>Builds the summary dialog view model for the displayed session, or null if none is eligible.</summary>
    public MeetingSummaryDialogViewModel? CreateSummaryDialogVm()
        => BuildDisplayedContext() is { } ctx
            ? new MeetingSummaryDialogViewModel(ctx, _summary, _loc, _settingsStore, _translationOptions, _summaryOptions, _logger)
            : null;

    public void OpenSummaryFile()
    {
        if (BuildDisplayedContext() is { } c)
        {
            _summary.OpenSummary(c.SessionDirectory);
        }
    }

    public void ShowSummaryFolder()
    {
        if (BuildDisplayedContext() is { } c)
        {
            _summary.ShowInFolder(c.SessionDirectory);
        }
    }

    private void RaiseSummaryState()
    {
        OnPropertyChanged(nameof(CanGenerateSummary));
        OnPropertyChanged(nameof(SummaryFileExists));
        OnPropertyChanged(nameof(CanDeleteDisplayedSession));
    }

    // ---- UI-R5A audio inputs (start dialog helpers) ----------------------

    /// <summary>Active microphone (input) devices with stable ids, for the start dialog picker.</summary>
    public IReadOnlyList<AudioCaptureDeviceInfo> GetMicDevices() => _devices.GetCaptureDevices();

    /// <summary>A fresh live input-level meter for the start dialog (the caller disposes it on close).</summary>
    public MicrophoneLevelMeter CreateLevelMeter() => _meterFactory();

    /// <summary>Persists the confirmed audio inputs (non-secret) so the choice survives a restart.</summary>
    public void PersistAudioOptions(MeetingAudioOptions options)
    {
        try
        {
            var (existing, _) = _settingsStore.Load();
            _settingsStore.Save(existing with
            {
                RecordSystemAudio = options.RecordSystemAudio,
                RecordMicrophone = options.RecordMicrophone,
                MicrophoneDeviceId = options.MicrophoneDeviceId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting audio options failed.");
        }
    }

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
                _ = LoadRecentMeetingsAsync();
            }

            OnPropertyChanged(nameof(HasNoSession));
            RaiseTranslationDisplay(); // running↔idle changes which target is shown
            RaiseSummaryState();       // stopped session → summary becomes available
            LoadHistoryCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName is nameof(RealtimeCaptionViewModel.SelectedLanguage)
                 or nameof(RealtimeCaptionViewModel.SessionTargetLanguage))
        {
            RaiseTranslationDisplay(); // recognition-language / session target changed
        }
    }

    private void OnTimelineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingTimelineViewModel.FinalCount))
        {
            OnPropertyChanged(nameof(HasNoSession));
            RaiseSummaryState();
        }
        else if (e.PropertyName == nameof(MeetingTimelineViewModel.DisplayedSession))
        {
            RaiseSummaryState(); // history session loaded/cleared → summary target changed
        }
    }

    private void OnTranslationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslationViewModel.Enabled))
        {
            _ = ApplyLiveTranslationToggleAsync(Translation.Enabled);
        }

        if (e.PropertyName is nameof(TranslationViewModel.Enabled)
            or nameof(TranslationViewModel.IsConfigured)
            or nameof(TranslationViewModel.DirectionText)
            or nameof(TranslationViewModel.TargetLanguage))
        {
            OnPropertyChanged(nameof(TranslationNotConfiguredHint));
            RaiseTranslationDisplay();
        }
    }

    private async Task ApplyLiveTranslationToggleAsync(bool enabled)
    {
        try
        {
            await Realtime.SetLiveTranslationEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply the live translation toggle.");
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
