using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Core.Session;
using KikuCaption.Infrastructure.Diagnostics;
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
    private readonly Func<IAudioCaptureService> _captureFactory;
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
    private readonly ILogger<RealtimeCaptionViewModel> _logger;
    private readonly DispatcherTimer _metricsTimer;

    // Milestone 7: unified lifecycle + reproducible resource sampling.
    private readonly SessionStateMachine _sessionState = new();
    private readonly ProcessCpuSampler _mainCpu = new();
    private readonly ProcessCpuSampler _ffmpegCpu = new();
    private long _lastMp4Bytes;
    private DateTime _lastMp4SampleUtc = DateTime.UtcNow;
    private readonly HashSet<Guid> _activeTranslations = new();

    private RealtimeCaptionPipeline? _pipeline;
    private IAudioCaptureService? _capture;
    private IScreenRecorder? _screenRecorder;
    private FFmpegCapabilities? _capabilities;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _selectedLanguage = "ja";
    [ObservableProperty] private string _statusText = "未开始。开始后将捕获系统声音并显示实时字幕。";
    [ObservableProperty] private string _metricsText = string.Empty;

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

    // Milestone 7 unified lifecycle + health.
    [ObservableProperty] private string _sessionStateText = "空闲";
    [ObservableProperty] private string _healthText = string.Empty;
    [ObservableProperty] private string _preflightSummary = string.Empty;

    public RealtimeCaptionViewModel(
        Func<IAudioCaptureService> captureFactory,
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
        ILogger<RealtimeCaptionViewModel> logger)
    {
        _captureFactory = captureFactory;
        _pipelineFactory = pipelineFactory;
        _recorder = recorder;
        _storageOptions = storageOptions;
        _screenRecorderFactory = screenRecorderFactory;
        _capabilityProbe = capabilityProbe;
        _preflight = preflight;
        _recordingRuntime = recordingRuntime;
        _translation = translation;
        _translationOptions = translationOptions;
        _exporter = exporter;
        Overlay = overlay;
        Timeline = timeline;
        _logger = logger;
        _translation.OutcomeChanged += OnTranslationOutcome;
        _sessionState.StateChanged += (_, to) => Dispatch(() => SessionStateText = SessionStateLabel(to));
        RecorderStatus = recordingRuntime.FFmpegPath is null ? "未找到 FFmpeg（仅字幕，不录屏）" : "就绪";
        RefreshWindows();

        _recorder.SavedFinal += (_, _) => Dispatch(RefreshStorageStatus);
        _recorder.StorageFailed += (_, e) => Dispatch(() =>
        {
            StorageError = e.Message;
            StorageStatus = "存储错误";
        });
        _recorder.DiskLow += (_, _) => Dispatch(() =>
        {
            StorageError = "磁盘空间不足，已安全停止接收新字幕。";
            StorageStatus = "磁盘不足";
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

        ErrorMessage = null;
        StorageError = null;
        _sessionState.BeginPreflight();

        var root = _storageOptions.ResolveOutputRoot();
        bool recordingRequested = _recordingRuntime.FFmpegPath is not null;

        // Preflight before creating any session (Milestone 7 §2).
        var report = await _preflight.RunAsync(recordingRequested, SelectedCaptureType, SelectedWindow, CancellationToken.None);
        PreflightSummary = SummarizePreflight(report);
        if (report.HasBlocking)
        {
            ErrorMessage = "预检未通过：" + string.Join("；", report.Checks
                .Where(c => c.Severity == Core.Session.PreflightSeverity.Block).Select(c => c.Detail));
            StatusText = "预检存在阻断项，未开始。";
            _sessionState.TryTransition(Core.Enums.SessionState.Idle);
            return;
        }

        if (IsWindowCapture && string.IsNullOrWhiteSpace(SelectedWindow))
        {
            ErrorMessage = "已选择“指定窗口”，请先选择要录制的窗口。";
            _sessionState.TryTransition(Core.Enums.SessionState.Idle);
            return;
        }

        // Recording unavailable is a non-silent warning; captions continue (仅字幕) by default.
        bool recordThisSession = recordingRequested && report.RecordingAvailable;
        if (recordingRequested && !report.RecordingAvailable)
        {
            RecorderStatus = "录屏不可用（预检警告）——本次仅字幕继续。";
        }

        _sessionState.TryTransition(Core.Enums.SessionState.Starting);
        try
        {
            _cts = new CancellationTokenSource();
            _capture = _captureFactory();
            _pipeline = _pipelineFactory();
            _pipeline.PartialUpdated += OnPartial;
            _pipeline.FinalProduced += OnFinalProduced;
            _pipeline.Faulted += OnFaulted;

            Overlay.Clear();
            Overlay.IsVisible = true;
            Timeline.BeginSession(); // fresh full-meeting timeline (Milestone 3.1)
            StatusText = "正在启动 Worker 并加载模型（首次约 1–2 秒）……";

            await _pipeline.StartAsync(_capture.CaptureAsync(_cts.Token), SelectedLanguage, _cts.Token);

            // Create the session + directory and begin real-time persistence.
            var startedAt = DateTimeOffset.Now;
            var seed = new MeetingSession
            {
                Id = _pipeline.SessionId,
                StartedAt = startedAt,
                RecognitionLanguage = SelectedLanguage,
                OutputDirectory = root
            };
            var session = seed with { OutputDirectory = SessionPaths.BuildSessionDirectory(root, seed) };
            await _recorder.StartSessionAsync(session, CancellationToken.None);

            StorageSessionId = session.Id.ToString("N");
            StorageOutputDirectory = session.OutputDirectory;
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
            StatusText = $"实时字幕运行中（语言：{SelectedLanguage}）。";
            _metricsTimer.Start();
        }
        catch (Exception ex)
        {
            // A step failed after others started: roll back everything already started; already-saved
            // captions are never deleted (Milestone 7 §1).
            _logger.LogError(ex, "Failed to start realtime captioning.");
            ErrorMessage = "启动失败（已回滚已启动模块，已保存字幕保留）：" + ex.Message;
            StatusText = "启动失败，详见提示与日志。";
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
        StatusText = "正在停止并保存……";
        try
        {
            // Finalize the MP4 first, then captions + storage.
            await StopRecordingAsync();

            _cts?.Cancel();
            if (_pipeline is not null)
            {
                await _pipeline.StopAsync();
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
        StatusText = "已停止。字幕已保存到输出目录。";
    }

    /// <summary>Unified session state for tests/UI (Milestone 7).</summary>
    public Core.Enums.SessionState CurrentSessionState => _sessionState.State;

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

        if (_capture is not null)
        {
            try { await _capture.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "capture dispose"); }
            _capture = null;
        }

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
            RecorderStatus = "未找到 FFmpeg，仅字幕（未录屏）。";
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
                IncludeSystemAudio = true
            };

            _screenRecorder = _screenRecorderFactory();
            await _screenRecorder.StartAsync(options, CancellationToken.None);
            await _recorder.SetRecordingPathAsync(mp4);

            RecordingEncoder = encoder;
            RecordingFilePath = mp4;
            RecorderStatus = $"录屏中（编码器 {encoder}）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start screen recording.");
            RecordingError = "录屏启动失败：" + ex.Message + "（字幕将继续）";
            RecorderStatus = "录屏失败，仅字幕。";
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
            RecorderStatus = result.IsComplete
                ? $"录屏完成（{result.Encoder}，{result.FileSizeBytes / 1024 / 1024} MB）"
                : "录屏可能不完整（详见提示）。";
            if (!result.IsComplete)
            {
                RecordingError = result.Message;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping screen recording.");
            RecordingError = "停止录屏出错：" + ex.Message;
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
        bool willTranslate = _translationOptions.Enabled
            && string.Equals(SelectedLanguage, _translationOptions.SourceLanguage, StringComparison.OrdinalIgnoreCase);
        var display = willTranslate ? TranslationDisplayState.Translating : TranslationDisplayState.None;

        Dispatch(() =>
        {
            Overlay.AddFinal(segmentId, e.Text, translating: willTranslate);
            // Full-meeting timeline keeps every final (never trimmed). Arrival order == SQLite
            // SequenceNumber for this fresh session, so the on-screen order matches storage.
            Timeline.AppendLive(segmentId, createdAt, e.Text, display);
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
            Dispatch(() => StorageError = "保存字幕失败：" + ex.Message);
            return;
        }

        if (willTranslate)
        {
            try
            {
                await _translation.EnqueueAsync(segment, CancellationToken.None);
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
        ErrorMessage = "实时字幕中断：" + e.Message;
        StatusText = "已因错误停止。已保存的字幕仍在输出目录。";
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
        MetricsText = m is null
            ? string.Empty
            : $"partial={m.PartialCount}  final={m.FinalCount}  RTF={m.Rtf:0.00}  推理={m.LastInferenceMs}ms  " +
              $"队列={m.QueueDepthMs}ms  背压跳过={m.SkippedCycles}  " +
              // Audio-loss Hotfix diagnostics (numbers only, never caption text): received vs
              // finalized/pending, and the invariant that must always read 0 on the safe path.
              $"音频收到={m.AudioReceivedSeconds:0.0}s 已final={m.AudioFinalizedSeconds:0.0}s " +
              $"待处理={m.PendingAudioSeconds:0.0}s 丢弃(未提交)={m.AudioDiscardedUncommittedSeconds:0.0}s";

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

            HealthText = DiagnosticsFormatter.HealthLabel(snapshot, _storageOptions.MinimumFreeSpaceGb);
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

    private static string SessionStateLabel(Core.Enums.SessionState s) => s switch
    {
        Core.Enums.SessionState.Idle => "空闲",
        Core.Enums.SessionState.Preflight => "预检中",
        Core.Enums.SessionState.Starting => "启动中",
        Core.Enums.SessionState.Running => "运行中",
        Core.Enums.SessionState.Stopping => "停止中",
        Core.Enums.SessionState.Completed => "已完成",
        Core.Enums.SessionState.Faulted => "已故障",
        Core.Enums.SessionState.Recovering => "恢复中",
        _ => s.ToString()
    };

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
