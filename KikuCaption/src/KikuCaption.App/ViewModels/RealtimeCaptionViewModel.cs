using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.Audio.Mixing;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Core.Session;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Recording.CaptureTargets;
using KikuCaption.Recording.FFmpeg;
using KikuCaption.Speech.Streaming;
using KikuCaption.Storage;
using KikuCaption.Translation;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Drives the real-time captioning pipeline (Milestone 3) and connects its final segments to
/// storage (Milestone 4). Marshals pipeline/recorder events onto the UI thread. No recognition,
/// stabilization or persistence logic lives here — only orchestration.
/// </summary>
public partial class RealtimeCaptionViewModel : ObservableObject, IMeetingCaptureTargetSink
{
    private readonly Func<AudioMixOptions, SessionAudioMixer> _mixerFactory;
    private readonly Func<RealtimeCaptionPipeline> _pipelineFactory;
    private readonly SessionRecorder _recorder;
    private readonly StorageOptions _storageOptions;
    private readonly Func<IScreenRecorder> _screenRecorderFactory;
    private readonly FFmpegCapabilityProbe _capabilityProbe;
    private readonly RecordingRuntimeOptions _recordingRuntime;
    private readonly TranslationQueue _translation;
    private readonly TranslationOptions _translationOptions;
    private readonly ITranscriptExporter _exporter;
    private readonly PreflightService _preflight;
    private readonly LocalizationService _loc;
    private readonly ILogger<RealtimeCaptionViewModel> _logger;
    private readonly KikuCaption.App.Services.PostMeetingCorrectionService _correction;
    private readonly UserSettingsStore _userSettingsStore;
    private readonly DispatcherTimer _metricsTimer;

    // UI-R3: the long-lived status strings are held as resource keys/args and re-localized when the
    // language changes, so switching language refreshes the running page immediately.
    private string? _statusKey;
    private string? _recorderKey;
    private object?[] _recorderArgs = System.Array.Empty<object?>();
    private HealthState? _healthState;
    private string? _correctionStatusKey;

    // Milestone 7: unified lifecycle + reproducible resource sampling.
    private readonly SessionStateMachine _sessionState = new();
    private readonly ProcessCpuSampler _mainCpu = new();
    private readonly ProcessCpuSampler _ffmpegCpu = new();
    private long _lastMp4Bytes;
    private DateTime _lastMp4SampleUtc = DateTime.UtcNow;
    private readonly HashSet<Guid> _activeTranslations = new();

    // UI-R4A: immutable translation-direction snapshot for the running meeting (null when idle).
    private SessionTranslationOptions? _sessionTranslation;

    private RealtimeCaptionPipeline? _pipeline;
    private SessionAudioMixer? _mixer;
    private IAudioCaptureService? _recordingAudioSource;
    private IScreenRecorder? _screenRecorder;

    // UI-R5A meeting audio inputs (seeded from persisted settings; applied from the start dialog).
    private bool _recordSystemAudio = true;
    private bool _recordMicrophone = true;
    private string? _micDeviceId;
    private bool _autoCorrectThisSession = true;
    private FFmpegCapabilities? _capabilities;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLanguageDisplay))]
    private string _selectedLanguage = "ja";

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _metricsText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioQualityWarning))]
    private string _audioQualityWarning = string.Empty;

    public bool HasAudioQualityWarning => !string.IsNullOrWhiteSpace(AudioQualityWarning);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    // Milestone 4 storage status.
    [ObservableProperty] private string _storageSessionId = string.Empty;
    [ObservableProperty] private string _storageOutputDirectory = string.Empty;
    [ObservableProperty] private int _savedFinalCount;
    [ObservableProperty] private string _lastSavedText = string.Empty;
    [ObservableProperty] private string _storageStatus = "未开始";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorageError))]
    private string? _storageError;

    // Milestone 5 recording.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowCapture))]
    private string _selectedCaptureType = "screen"; // "screen" | "window"
    [ObservableProperty] private string? _selectedWindow;
    [ObservableProperty] private string _recorderStatus = "未开始";
    [ObservableProperty] private string _recordingEncoder = string.Empty;
    [ObservableProperty] private string _recordingFilePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecordingError))]
    private string? _recordingError;

    // Milestone 7 unified lifecycle + health (localized, UI-R3).
    [ObservableProperty] private string _sessionStateText = string.Empty;
    [ObservableProperty] private string _healthText = string.Empty;
    [ObservableProperty] private string _preflightSummary = string.Empty;
    [ObservableProperty] private string _correctionStatus = string.Empty;
    [ObservableProperty] private bool _isCorrectionRunning;

    public RealtimeCaptionViewModel(
        Func<AudioMixOptions, SessionAudioMixer> mixerFactory,
        Func<RealtimeCaptionPipeline> pipelineFactory,
        SessionRecorder recorder,
        StorageOptions storageOptions,
        Func<IScreenRecorder> screenRecorderFactory,
        FFmpegCapabilityProbe capabilityProbe,
        RecordingRuntimeOptions recordingRuntime,
        SubtitleOverlayViewModel overlay,
        MeetingTimelineViewModel timeline,
        TranslationQueue translation,
        TranslationOptions translationOptions,
        ITranscriptExporter exporter,
        PreflightService preflight,
        KikuCaption.App.Services.PostMeetingCorrectionService correction,
        UserSettingsStore userSettingsStore,
        LocalizationService localization,
        ILogger<RealtimeCaptionViewModel> logger)
    {
        _loc = localization;
        _mixerFactory = mixerFactory;
        _pipelineFactory = pipelineFactory;
        _recorder = recorder;
        _storageOptions = storageOptions;
        _screenRecorderFactory = screenRecorderFactory;
        _capabilityProbe = capabilityProbe;
        _preflight = preflight;
        _correction = correction;
        _userSettingsStore = userSettingsStore;
        _recordingRuntime = recordingRuntime;
        _translation = translation;
        _translationOptions = translationOptions;
        _exporter = exporter;
        Overlay = overlay;
        Timeline = timeline;
        _logger = logger;
        _translation.OutcomeChanged += OnTranslationOutcome;
        _sessionState.StateChanged += (_, to) => Dispatch(() =>
        {
            SessionStateText = _loc["Session.State." + to];
            OnPropertyChanged(nameof(CanStartMeeting)); // enable/disable the Start button across the whole cycle
        });
        SessionStateText = _loc["Session.State." + _sessionState.State];
        SetStatus("Status.Idle");
        SetRecorder(recordingRuntime.FFmpegPath is null ? "Recorder.NoFFmpeg" : "Recorder.Ready");
        // Re-localize the long-lived status strings live when the UI language changes (UI-R3).
        _loc.LanguageChanged += (_, _) => Dispatch(RefreshLocalizedText);
        // UI-R4A: the live translation source always follows the recognition language (idle preview).
        _translationOptions.SourceLanguage = SelectedLanguage;
        RefreshWindows();

        _recorder.SavedFinal += (_, _) => Dispatch(RefreshStorageStatus);
        _recorder.StorageFailed += (_, e) => Dispatch(() =>
        {
            StorageError = e.Message;
            StorageStatus = "存储错误";
        });
        _recorder.DiskLow += (_, _) => Dispatch(() =>
        {
            StorageError = _loc["Error.DiskLow"];
            StorageStatus = _loc["Health.LowDisk"];
            _ = StopAsync();
        });

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
    }

    public SubtitleOverlayViewModel Overlay { get; }

    /// <summary>Full-meeting subtitle timeline (Milestone 3.1): every final, first to last.</summary>
    public MeetingTimelineViewModel Timeline { get; }

    public IReadOnlyList<string> Languages { get; } = new[] { "ja", "zh" };

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStorageError => !string.IsNullOrEmpty(StorageError);

    public bool HasRecordingError => !string.IsNullOrEmpty(RecordingError);

    public bool IsWindowCapture => SelectedCaptureType == "window";

    /// <summary>Titles of visible top-level windows for the window-capture picker.</summary>
    public ObservableCollection<string> Windows { get; } = new();

    /// <summary>The live capture target — read to seed the start dialog draft (UI-R2).</summary>
    public MeetingCaptureTarget CaptureTarget => new(SelectedCaptureType, IsWindowCapture ? SelectedWindow : null);

    /// <summary>
    /// Applies a chosen capture target. This is the only path the start dialog uses to write the
    /// target, and it is invoked once, on confirm — never during editing (UI-R2 dialog-draft fix).
    /// </summary>
    public void ApplyCaptureTarget(MeetingCaptureTarget target)
    {
        SelectedCaptureType = target.CaptureType;
        SelectedWindow = target.WindowTitle;
    }

    /// <summary>The live audio-input choice (UI-R5A) — used to seed the start dialog draft.</summary>
    public MeetingAudioOptions AudioOptions => new(_recordSystemAudio, _recordMicrophone, _micDeviceId);

    /// <summary>Applies the chosen audio inputs (system audio / microphone / device) to the live state.</summary>
    public void ApplyAudioOptions(MeetingAudioOptions options)
    {
        _recordSystemAudio = options.RecordSystemAudio;
        _recordMicrophone = options.RecordMicrophone;
        _micDeviceId = options.MicrophoneDeviceId;
    }

    // UI-R4A: keep the translation source following the recognition language (idle direction preview).
    partial void OnSelectedLanguageChanged(string value) => _translationOptions.SourceLanguage = value;

    /// <summary>Localized display name of the recognition language (e.g. 日本語 / Japanese).</summary>
    public string SelectedLanguageDisplay => _loc["Lang." + SelectedLanguage];

    /// <summary>The running session's snapshot source language (recognition), or null when idle (UI-R4A).</summary>
    public string? SessionSourceLanguage => _sessionTranslation?.SourceLanguage;

    /// <summary>The running session's snapshot target language, or null when idle (UI-R4A).</summary>
    public string? SessionTargetLanguage => _sessionTranslation?.TargetLanguage;

    /// <summary>
    /// Applies the home-page checkbox to the current running session immediately. Direction, model
    /// and prompt version remain the session snapshot; only the live enabled flag changes.
    /// </summary>
    public Task SetLiveTranslationEnabledAsync(bool enabled)
    {
        _translationOptions.Enabled = enabled;
        if (_sessionTranslation is null)
        {
            return Task.CompletedTask;
        }

        _sessionTranslation = _sessionTranslation with { Enabled = enabled };
        OnPropertyChanged(nameof(SessionTargetLanguage));
        if (!IsRunning || !_recorder.IsRunning)
        {
            return Task.CompletedTask;
        }

        return _translation.SetSessionEnabledAsync(_recorder.SessionId, enabled, CancellationToken.None);
    }

    // Status/recorder text are stored as resource keys so they re-localize on a language switch.
    private void SetStatus(string key)
    {
        _statusKey = key;
        StatusText = ComputeStatusText();
    }

    private string ComputeStatusText() => _statusKey switch
    {
        null => string.Empty,
        "Status.Running" => string.Format(_loc["Status.Running"], SelectedLanguageDisplay),
        _ => _loc[_statusKey]
    };

    private void SetRecorder(string key, params object?[] args)
    {
        _recorderKey = key;
        _recorderArgs = args;
        RecorderStatus = args.Length == 0 ? _loc[key] : string.Format(_loc[key], args);
    }

    private void RefreshLocalizedText()
    {
        SessionStateText = _loc["Session.State." + _sessionState.State];
        StatusText = ComputeStatusText();
        if (_recorderKey is not null)
        {
            RecorderStatus = _recorderArgs.Length == 0 ? _loc[_recorderKey] : string.Format(_loc[_recorderKey], _recorderArgs);
        }
        if (_healthState is not null)
        {
            HealthText = _loc["Health." + _healthState];
        }
        if (_correctionStatusKey is not null) CorrectionStatus = _loc[_correctionStatusKey];
        if (_mixer is { SpeechDroppedChunks: > 0 } mixer)
        {
            AudioQualityWarning = string.Format(_loc["AudioQuality.Dropped"], mixer.SpeechDroppedChunks);
        }
        OnPropertyChanged(nameof(SelectedLanguageDisplay));
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        Windows.Clear();
        foreach (var window in WindowEnumerator.EnumerateWindows())
        {
            Windows.Add(window.Title);
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        // Milestone 7: reject a duplicate start while a session is busy.
        if (!_sessionState.CanStart || IsRunning)
        {
            return;
        }

        _correction.CancelCurrent();
        _autoCorrectThisSession = _userSettingsStore.Load().Settings.AutoCorrectAfterMeeting;
        ErrorMessage = null;
        StorageError = null;
        AudioQualityWarning = string.Empty;
        _sessionState.BeginPreflight();

        var root = _storageOptions.ResolveOutputRoot();
        bool recordingRequested = _recordingRuntime.FFmpegPath is not null;

        // Preflight before creating any session (Milestone 7 §2).
        var report = await _preflight.RunAsync(recordingRequested, SelectedCaptureType, SelectedWindow, CancellationToken.None);
        PreflightSummary = SummarizePreflight(report);
        if (report.HasBlocking)
        {
            ErrorMessage = _loc["Error.PreflightBlocked"] + string.Join("；", report.Checks
                .Where(c => c.Severity == Core.Session.PreflightSeverity.Block).Select(c => c.Detail));
            SetStatus("Status.PreflightBlocked");
            _sessionState.TryTransition(Core.Enums.SessionState.Idle);
            return;
        }

        if (IsWindowCapture && string.IsNullOrWhiteSpace(SelectedWindow))
        {
            ErrorMessage = _loc["Error.WindowRequired"];
            _sessionState.TryTransition(Core.Enums.SessionState.Idle);
            return;
        }

        // Recording unavailable is a non-silent warning; captions continue (仅字幕) by default.
        bool recordThisSession = recordingRequested && report.RecordingAvailable;
        if (recordingRequested && !report.RecordingAvailable)
        {
            SetRecorder("Recorder.Unavailable");
        }

        _sessionState.TryTransition(Core.Enums.SessionState.Starting);
        try
        {
            _cts = new CancellationTokenSource();

            // UI-R5A: one session mixer opens a single system loopback + (optionally) the microphone,
            // mixes to 16k/mono/int16, and fans the SAME mixed PCM out to the caption pipeline and the
            // recorder — so exactly one loopback exists and the mic reaches both captions and meeting.mp4.
            var mixOptions = new AudioMixOptions(_recordSystemAudio, _recordMicrophone, _micDeviceId);
            _mixer = _mixerFactory(mixOptions);
            _recordingAudioSource = recordThisSession ? _mixer.CreateRecordingSource() : null;
            _mixer.Start(_cts.Token);

            _pipeline = _pipelineFactory();
            _pipeline.PartialUpdated += OnPartial;
            _pipeline.FinalProduced += OnFinalProduced;
            _pipeline.Faulted += OnFaulted;

            // UI-R3: clear prior lines and apply the DefaultShowOverlay preference for the new
            // meeting. A user's manual show/hide during the session is never overridden afterwards.
            Overlay.PrepareForNewSession();
            Timeline.BeginSession(); // fresh full-meeting timeline (Milestone 3.1)
            SetStatus("Status.Loading");

            await _pipeline.StartAsync(_mixer.SpeechSource.CaptureAsync(_cts.Token), SelectedLanguage, _cts.Token);

            // UI-R4A: snapshot the translation direction now (source = recognition language; target =
            // the configured target). Immutable for the whole meeting, even if the user later changes
            // the target in settings. Same-language → EffectiveEnabled is false (no jobs, no API).
            _sessionTranslation = new SessionTranslationOptions(
                SourceLanguage: SelectedLanguage,
                TargetLanguage: string.IsNullOrWhiteSpace(_translationOptions.TargetLanguage) ? "zh" : _translationOptions.TargetLanguage,
                Enabled: _translationOptions.Enabled,
                Model: _translationOptions.Model,
                PromptVersion: TranslationPrompt.Version);
            OnPropertyChanged(nameof(SessionTargetLanguage));

            // Create the session + directory and begin real-time persistence.
            var startedAt = DateTimeOffset.Now;
            var seed = new MeetingSession
            {
                Id = _pipeline.SessionId,
                StartedAt = startedAt,
                RecognitionLanguage = SelectedLanguage,
                OutputDirectory = root,
                TranslationEnabled = _sessionTranslation.EffectiveEnabled,
                TranslationSource = _sessionTranslation.SourceLanguage,
                TranslationTarget = _sessionTranslation.TargetLanguage,
                TranslationModel = _sessionTranslation.Model
            };
            var session = seed with { OutputDirectory = SessionPaths.BuildSessionDirectory(root, seed) };
            await _recorder.StartSessionAsync(session, CancellationToken.None);

            StorageSessionId = session.Id.ToString("N");
            StorageOutputDirectory = session.OutputDirectory;
            // UI-R5C: the timeline now targets THIS live session (real id + directory) for summaries.
            Timeline.SetLiveSession(session.Id, session.OutputDirectory, startedAt, SelectedLanguage);
            SavedFinalCount = 0;
            StorageStatus = "录制中（实时保存）";

            // Milestone 5: start screen recording into the same session directory. A recording
            // failure does not stop captions (they keep saving).
            if (recordThisSession)
            {
                await StartRecordingAsync(session.OutputDirectory);
            }

            IsRunning = true;
            _sessionState.TryTransition(Core.Enums.SessionState.Running);
            SetStatus("Status.Running");
            _metricsTimer.Start();
        }
        catch (Exception ex)
        {
            // A step failed after others started: roll back everything already started; already-saved
            // captions are never deleted (Milestone 7 §1).
            _logger.LogError(ex, "Failed to start realtime captioning.");
            ErrorMessage = _loc["Error.StartFailed"] + ex.Message;
            SetStatus("Status.StartFailed");
            IsRunning = false;
            _sessionState.TryTransition(Core.Enums.SessionState.Stopping);
            await CleanupAsync();
            _sessionState.TryTransition(Core.Enums.SessionState.Faulted);
            _sessionState.TryTransition(Core.Enums.SessionState.Idle);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        // Idempotent: a second stop while already stopping/idle is a harmless no-op (Milestone 7 §1).
        if (!_sessionState.CanStop && _sessionState.State != Core.Enums.SessionState.Stopping)
        {
            return;
        }

        _sessionState.RequestStop();
        _metricsTimer.Stop();
        SetStatus("Status.Stopping");
        try
        {
            // Finalize the MP4 first (drains the recorder's mixed-audio branch + validates), then stop
            // the mixer/captions so the last mixed PCM is finalized, then storage. Cancelling _cts stops
            // the mixer pumps AND the pipeline's ingest of the mixed stream (same as the old loopback).
            await StopRecordingAsync();

            _cts?.Cancel();
            if (_pipeline is not null)
            {
                await _pipeline.StopAsync();
            }

            if (_mixer is not null)
            {
                await _mixer.StopAsync();
            }

            if (_recorder.IsRunning)
            {
                await _recorder.StopSessionAsync(DateTimeOffset.Now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping realtime captioning.");
        }

        await CleanupAsync();
        IsRunning = false;
        RefreshStorageStatus();
        if (StorageError is null)
        {
            StorageStatus = "已停止并保存";
        }

        UpdateMetrics();
        _sessionState.TryTransition(Core.Enums.SessionState.Completed);
        _sessionState.TryTransition(Core.Enums.SessionState.Idle);
        SetStatus("Status.Stopped");

        if (_autoCorrectThisSession && Guid.TryParse(StorageSessionId, out var completedSessionId) &&
            !string.IsNullOrWhiteSpace(RecordingFilePath) && File.Exists(RecordingFilePath) &&
            !string.IsNullOrWhiteSpace(StorageOutputDirectory))
        {
            StartPostMeetingCorrection(new KikuCaption.App.Services.PostMeetingCorrectionRequest(
                completedSessionId, RecordingFilePath, StorageOutputDirectory, SelectedLanguage));
        }
    }

    private void StartPostMeetingCorrection(KikuCaption.App.Services.PostMeetingCorrectionRequest request)
    {
        IsCorrectionRunning = true;
        _correctionStatusKey = "Correction.Running";
        CorrectionStatus = _loc[_correctionStatusKey];
        _ = RunPostMeetingCorrectionAsync(request);
    }

    private async Task RunPostMeetingCorrectionAsync(KikuCaption.App.Services.PostMeetingCorrectionRequest request)
    {
        try
        {
            await _correction.RunAsync(request, CancellationToken.None).ConfigureAwait(false);
            Dispatch(() =>
            {
                _correctionStatusKey = "Correction.Completed";
                CorrectionStatus = _loc[_correctionStatusKey];
                IsCorrectionRunning = false;
            });
        }
        catch (OperationCanceledException)
        {
            Dispatch(() =>
            {
                _correctionStatusKey = "Correction.Cancelled";
                CorrectionStatus = _loc[_correctionStatusKey];
                IsCorrectionRunning = false;
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-meeting correction failed for session {SessionId}.", request.SessionId);
            Dispatch(() =>
            {
                _correctionStatusKey = "Correction.Failed";
                CorrectionStatus = _loc[_correctionStatusKey];
                IsCorrectionRunning = false;
            });
        }
    }

    /// <summary>Unified session state for tests/UI (Milestone 7).</summary>
    public Core.Enums.SessionState CurrentSessionState => _sessionState.State;

    /// <summary>True only when a NEW meeting may be started (idle/completed/faulted) — drives the
    /// Start button so it is clearly disabled through the whole preflight→running→stopping cycle,
    /// not just while running (UI feedback fix).</summary>
    public bool CanStartMeeting => _sessionState.CanStart;

    [RelayCommand]
    private void ToggleOverlay() => Overlay.IsVisible = !Overlay.IsVisible;

    private async Task CleanupAsync()
    {
        if (_pipeline is not null)
        {
            _pipeline.PartialUpdated -= OnPartial;
            _pipeline.FinalProduced -= OnFinalProduced;
            _pipeline.Faulted -= OnFaulted;
            _pipeline = null;
        }

        if (_mixer is not null)
        {
            try { await _mixer.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "mixer dispose"); }
            _mixer = null;
        }

        _recordingAudioSource = null;

        if (_screenRecorder is not null)
        {
            try { await _screenRecorder.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "recorder dispose"); }
            _screenRecorder = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task StartRecordingAsync(string sessionDirectory)
    {
        RecordingError = null;
        RecordingFilePath = string.Empty;
        RecordingEncoder = string.Empty;

        if (_recordingRuntime.FFmpegPath is null)
        {
            SetRecorder("Recorder.NoFFmpegShort");
            return;
        }

        try
        {
            _capabilities ??= await _capabilityProbe.ProbeAsync(_recordingRuntime.FFmpegPath, CancellationToken.None);
            var encoder = _capabilities.HasQuickSync && _recordingRuntime.PreferredEncoder == "h264_qsv"
                ? "h264_qsv"
                : _recordingRuntime.FallbackEncoder;

            var mp4 = Path.Combine(sessionDirectory, "meeting.mp4");
            var options = new RecordingOptions
            {
                CaptureType = IsWindowCapture ? CaptureTargetType.Window : CaptureTargetType.Screen,
                TargetTitle = IsWindowCapture ? SelectedWindow : null,
                OutputPath = mp4,
                FFmpegPath = _recordingRuntime.FFmpegPath,
                FrameRate = _recordingRuntime.FrameRate,
                Encoder = encoder,
                IncludeSystemAudio = true,
                // UI-R5A: feed the recorder the SAME mixed PCM (system + mic) the captions use, instead
                // of opening a second loopback. Null falls back to the recorder's own loopback (legacy).
                ExternalAudioSource = _recordingAudioSource
            };

            _screenRecorder = _screenRecorderFactory();
            await _screenRecorder.StartAsync(options, CancellationToken.None);
            await _recorder.SetRecordingPathAsync(mp4);

            RecordingEncoder = encoder;
            RecordingFilePath = mp4;
            SetRecorder("Recorder.Recording", encoder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start screen recording.");
            RecordingError = _loc["Error.RecordingStartFailed"] + ex.Message;
            SetRecorder("Recorder.Failed");
            if (_screenRecorder is not null)
            {
                try { await _screenRecorder.DisposeAsync(); } catch { /* ignore */ }
                _screenRecorder = null;
            }
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_screenRecorder is null)
        {
            return;
        }

        try
        {
            var result = await _screenRecorder.StopAsync(CancellationToken.None);
            RecordingFilePath = result.OutputPath;
            if (result.IsComplete)
            {
                SetRecorder("Recorder.Done", result.Encoder, result.FileSizeBytes / 1024 / 1024);
            }
            else
            {
                SetRecorder("Recorder.Incomplete");
                RecordingError = result.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping screen recording.");
            RecordingError = _loc["Error.RecordingStopFailed"] + ex.Message;
        }
        finally
        {
            try { await _screenRecorder.DisposeAsync(); } catch { /* ignore */ }
            _screenRecorder = null;
        }
    }

    private void OnPartial(object? sender, CaptionPartialEventArgs e) => Dispatch(() =>
    {
        Overlay.SetPartial(e.PartialText);
        Timeline.SetPartial(e.PartialText); // bottom "recognizing" line only; not a history entry
    });

    // Single final handler: one SegmentId ties the UI card, the persisted row, and the translation
    // job together, so a translation returning later updates the exact same card (M3.1 + M6).
    private void OnFinalProduced(object? sender, CaptionFinalEventArgs e)
    {
        var segmentId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Now;
        // UI-R4A: translate only when the session snapshot's direction is effectively enabled.
        bool willTranslate = _sessionTranslation?.EffectiveEnabled == true;
        var display = willTranslate ? TranslationDisplayState.Translating : TranslationDisplayState.None;

        Dispatch(() =>
        {
            Overlay.AddFinal(segmentId, e.Text, translating: willTranslate);
            // Full-meeting timeline keeps every final (never trimmed). Arrival order == SQLite
            // SequenceNumber for this fresh session, so the on-screen order matches storage.
            Timeline.AppendLive(segmentId, createdAt, e.Text, display, e.StartTime, e.EndTime);
        });

        _ = PersistAndTranslateAsync(segmentId, createdAt, e, willTranslate);
    }

    // Persists the ORIGINAL immediately, then enqueues translation (ja only). async: back-pressures
    // the pipeline's background final path, never the UI thread; recording of the original is never
    // blocked by translation.
    private async Task PersistAndTranslateAsync(Guid segmentId, DateTimeOffset createdAt, CaptionFinalEventArgs e, bool willTranslate)
    {
        if (!_recorder.IsRunning)
        {
            return;
        }

        var segment = new TranscriptSegment
        {
            Id = segmentId,
            SessionId = _recorder.SessionId,
            StartTime = e.StartTime,
            EndTime = e.EndTime,
            Language = SelectedLanguage,
            Text = e.Text,
            Status = TranscriptStatus.Final,
            CreatedAt = createdAt
        };

        try
        {
            await _recorder.RecordFinalAsync(segment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist final segment (len={Length}).", e.Text.Length);
            Dispatch(() => StorageError = _loc["Error.SaveCaptionFailed"] + ex.Message);
            return;
        }

        if (willTranslate && _sessionTranslation is not null)
        {
            try
            {
                await _translation.EnqueueAsync(segment, _sessionTranslation, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue translation (segment {SegmentId}).", segmentId);
            }
        }
    }

    // Translation lifecycle → update the matching card in place (no new card, no forced scroll) and
    // refresh translation.srt on success. Marshalled to the UI thread.
    private void OnTranslationOutcome(object? sender, TranslationOutcome outcome)
    {
        var (state, translating) = outcome.State switch
        {
            TranslationJobState.Succeeded => (TranslationDisplayState.Translated, false),
            TranslationJobState.FailedPermanent => (TranslationDisplayState.Failed, false),
            TranslationJobState.Pending or TranslationJobState.InProgress or TranslationJobState.RetryScheduled
                => (TranslationDisplayState.Translating, true),
            _ => (TranslationDisplayState.None, false)
        };

        Dispatch(() =>
        {
            Timeline.ApplyTranslation(outcome.SegmentId, state, outcome.Translation);
            Overlay.ApplyTranslation(outcome.SegmentId, outcome.Translation, translating);
            // Track active translations for the diagnostics queue-depth metric (Milestone 7 §5).
            if (translating) { _activeTranslations.Add(outcome.SegmentId); }
            else { _activeTranslations.Remove(outcome.SegmentId); }
        });

        if (outcome.State == TranslationJobState.Succeeded)
        {
            _ = ReexportTranslationsAsync();
        }
    }

    private async Task ReexportTranslationsAsync()
    {
        try
        {
            if (_recorder.SessionId != Guid.Empty && !string.IsNullOrEmpty(_recorder.OutputDirectory))
            {
                await _exporter.ExportAsync(_recorder.SessionId, _recorder.OutputDirectory, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-export after translation.");
        }
    }

    private void OnFaulted(object? sender, CaptionFaultedEventArgs e) => Dispatch(() =>
    {
        _metricsTimer.Stop();
        IsRunning = false;
        // Recognition faulted: mark the session Faulted (already-saved captions are kept).
        _sessionState.TryTransition(Core.Enums.SessionState.Faulted);
        _sessionState.TryTransition(Core.Enums.SessionState.Idle);
        ErrorMessage = _loc["Error.Fault"] + e.Message;
        SetStatus("Status.Faulted");
    });

    private void RefreshStorageStatus()
    {
        SavedFinalCount = (int)_recorder.SavedFinalCount;
        if (_recorder.LastSavedAt is { } t)
        {
            LastSavedText = t.LocalDateTime.ToString("HH:mm:ss");
        }

        if (_recorder.StorageError is { } err)
        {
            StorageError = err;
        }
    }

    private void UpdateMetrics()
    {
        RefreshStorageStatus();

        var m = _pipeline?.CurrentMetrics;
        var speechDropped = _mixer?.SpeechDroppedChunks ?? 0;
        AudioQualityWarning = speechDropped > 0
            ? string.Format(_loc["AudioQuality.Dropped"], speechDropped)
            : string.Empty;
        MetricsText = m is null
            ? string.Empty
            : $"partial={m.PartialCount}  final={m.FinalCount}  RTF={m.Rtf:0.00}  推理={m.LastInferenceMs}ms  " +
              $"队列={m.QueueDepthMs}ms  背压跳过={m.SkippedCycles}  " +
              // Audio-loss Hotfix diagnostics (numbers only, never caption text): received vs
              // finalized/pending, and the invariant that must always read 0 on the safe path.
              $"音频收到={m.AudioReceivedSeconds:0.0}s 已final={m.AudioFinalizedSeconds:0.0}s " +
              $"待处理={m.PendingAudioSeconds:0.0}s 丢弃(未提交)={m.AudioDiscardedUncommittedSeconds:0.0}s " +
              $"识别分支丢块={speechDropped}";

        // Milestone 7: reproducible resource sampling (main + ffmpeg CPU/mem) → redacted log + health.
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var root = _storageOptions.ResolveOutputRoot();
            System.Diagnostics.Process? ffmpeg = null;
            if (_screenRecorder?.RecordingProcessId is { } pid)
            {
                try { ffmpeg = System.Diagnostics.Process.GetProcessById(pid); } catch { ffmpeg = null; }
            }

            var snapshot = new DiagnosticsSnapshot
            {
                MainCpuPercent = _mainCpu.Sample(self),
                FfmpegCpuPercent = ffmpeg is null ? null : _ffmpegCpu.Sample(ffmpeg),
                MainWorkingSet = ProcessCpuSampler.WorkingSetBytes(self),
                FfmpegWorkingSet = ProcessCpuSampler.WorkingSetBytes(ffmpeg),
                Rtf = m?.Rtf,
                LastInferenceMs = m?.LastInferenceMs,
                AudioQueueDepthMs = (int)(m?.QueueDepthMs ?? 0),
                TranslationQueueDepth = _activeTranslations.Count,
                FreeDiskGb = DiskSpace.GetFreeGb(root),
                Mp4Bytes = SampleMp4(out var growth),
                Mp4GrowthKbPerSec = growth
            };
            ffmpeg?.Dispose();

            _healthState = DiagnosticsFormatter.HealthOf(snapshot, _storageOptions.MinimumFreeSpaceGb);
            HealthText = _loc["Health." + _healthState];
            if (IsRunning)
            {
                _logger.LogInformation(DiagnosticsFormatter.ToLogLine(snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resource sampling skipped.");
        }
    }

    private long SampleMp4(out double growthKbPerSec)
    {
        growthKbPerSec = 0;
        long bytes = 0;
        try
        {
            if (!string.IsNullOrEmpty(RecordingFilePath) && File.Exists(RecordingFilePath))
            {
                bytes = new FileInfo(RecordingFilePath).Length;
                var now = DateTime.UtcNow;
                var secs = (now - _lastMp4SampleUtc).TotalSeconds;
                if (secs > 0 && _lastMp4Bytes > 0 && bytes >= _lastMp4Bytes)
                {
                    growthKbPerSec = (bytes - _lastMp4Bytes) / 1024.0 / secs;
                }

                _lastMp4Bytes = bytes;
                _lastMp4SampleUtc = now;
            }
        }
        catch { /* best effort */ }

        return bytes;
    }

    private static string SummarizePreflight(Core.Session.PreflightReport r)
    {
        int block = r.Checks.Count(c => c.Severity == Core.Session.PreflightSeverity.Block);
        int warn = r.Checks.Count(c => c.Severity == Core.Session.PreflightSeverity.Warn);
        return $"预检：阻断 {block}、警告 {warn}；录屏{(r.RecordingAvailable ? "可用" : "不可用")}、翻译{(r.TranslationAvailable ? "可用" : "关闭/不可用")}。";
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
