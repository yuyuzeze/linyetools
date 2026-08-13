using System.IO;
using System.Windows;
using KikuCaption.App.Diagnostics;
using KikuCaption.App.Navigation;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.App.Views;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Audio.DependencyInjection;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.DependencyInjection;
using KikuCaption.Infrastructure.Logging;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.DependencyInjection;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using KikuCaption.Speech.Worker;
using KikuCaption.Recording.DependencyInjection;
using KikuCaption.Recording.FFmpeg;
using KikuCaption.Storage;
using KikuCaption.Storage.DependencyInjection;
using KikuCaption.Translation;
using KikuCaption.Translation.DependencyInjection;
using KikuCaption.Summarization.DependencyInjection;
using KikuCaption.App.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace KikuCaption.App;

/// <summary>
/// WPF entry point. Builds the Generic Host (DI, configuration, Serilog logging),
/// validates configuration, then shows the main window. Startup failures are shown in a
/// dialog instead of crashing (PROJECT.md 17, M0).
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .UseContentRoot(AppContext.BaseDirectory)
                .UseSerilog((context, _, loggerConfiguration) =>
                    SerilogConfigurator.Configure(
                        loggerConfiguration,
                        context.Configuration,
                        Path.Combine(AppContext.BaseDirectory, "logs")))
                .ConfigureServices((context, services) =>
                {
                    services.AddKikuCaptionInfrastructure(context.Configuration);
                    services.AddKikuCaptionAudio();

                    var speechSettings = context.Configuration.GetSection("Speech").Get<SpeechSettings>() ?? new SpeechSettings();
                    var subtitleSettings = context.Configuration.GetSection("Subtitle").Get<SubtitleSettings>() ?? new SubtitleSettings();
                    var whisperOptions = BuildWhisperWorkerOptions(speechSettings);
                    services.AddSingleton(whisperOptions); // for Milestone 7 preflight
                    services.AddKikuCaptionSpeech(whisperOptions);

                    // One provider shared by the real-time pipeline AND the WAV entry point: base
                    // options (model/device/compute/beam/cache) + per-language decoding context, so a
                    // language never receives another language's prompt/hotwords, and there is a single
                    // source of truth for the SpeechOptions construction.
                    var baseSpeechOptions = new SpeechOptions
                    {
                        Model = speechSettings.Model,
                        ComputeType = speechSettings.ComputeType,
                        BeamSize = speechSettings.BeamSize,
                        Language = speechSettings.Language,
                        ModelCacheDirectory = whisperOptions.ModelCacheDirectory
                    };
                    var contexts = new Dictionary<string, SpeechContext>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (lang, ctx) in speechSettings.Contexts)
                    {
                        contexts[lang] = new SpeechContext(
                            string.IsNullOrWhiteSpace(ctx.InitialPrompt) ? null : ctx.InitialPrompt,
                            Hotwords.Normalize(ctx.Hotwords)); // count/length/total limits enforced here
                    }

                    // UI-R4B: the appsettings Contexts seed two read-only built-in dictionaries in the
                    // per-user store (%LOCALAPPDATA%). The store is the single source of truth for the
                    // ACTIVE dictionary per language; the provider snapshots it at each session start.
                    services.AddSingleton<ISpeechDictionaryStore>(sp => new SpeechDictionaryStore(
                        SpeechDictionaryStore.DefaultDirectory,
                        contexts,
                        sp.GetRequiredService<ILogger<SpeechDictionaryStore>>()));

                    services.AddSingleton<ISpeechOptionsProvider>(sp =>
                        new SpeechOptionsProvider(baseSpeechOptions, sp.GetRequiredService<ISpeechDictionaryStore>()));

                    // Progressive caption options (validated at startup) — all tunables now mapped.
                    var progressive = new ProgressiveCaptionOptions
                    {
                        WindowSeconds = speechSettings.WindowSeconds,
                        OverlapSeconds = speechSettings.OverlapSeconds,
                        SilenceFinalMs = speechSettings.SilenceFinalMs,
                        StableRepeatCount = speechSettings.StableRepeatCount,
                        MaxSentenceSeconds = speechSettings.MaxSentenceSeconds,
                        MaxWaitSeconds = speechSettings.MaxWaitSeconds,
                        MaxLines = Math.Clamp(subtitleSettings.MaxLines, 2, 5),
                        // Hotfix safety switch: Validate() below refuses true (data-loss risk).
                        UseExperimentalSlidingWindow = speechSettings.UseExperimentalSlidingWindow
                    };
                    progressive.Validate();
                    services.AddSingleton(progressive);
                    services.AddSingleton(subtitleSettings);

                    services.AddTransient(sp => new RealtimeCaptionPipeline(
                        () => sp.GetRequiredService<ISpeechRecognizer>(),
                        sp.GetRequiredService<ProgressiveCaptionOptions>(),
                        sp.GetRequiredService<ISpeechOptionsProvider>(),
                        sp.GetRequiredService<ILogger<RealtimeCaptionPipeline>>()));
                    services.AddSingleton<Func<RealtimeCaptionPipeline>>(sp => () => sp.GetRequiredService<RealtimeCaptionPipeline>());

                    // Storage (Milestone 4).
                    var storageSettings = context.Configuration.GetSection("Storage").Get<StorageSettings>() ?? new StorageSettings();
                    var storageOptions = new StorageOptions
                    {
                        OutputDirectory = storageSettings.OutputDirectory,
                        BaseDirectory = AppContext.BaseDirectory,
                        MinimumFreeSpaceGb = storageSettings.MinimumFreeSpaceGb
                    };
                    services.AddKikuCaptionStorage(storageOptions, AppVersion());

                    // Recording (Milestone 5): locate FFmpeg and expose runtime options to the UI.
                    services.AddKikuCaptionRecording();
                    var recordingSettings = context.Configuration.GetSection("Recording").Get<RecordingSettings>() ?? new RecordingSettings();
                    var ffmpegPath = FFmpegLocator.LocateFFmpeg(recordingSettings.FFmpegPath, AppContext.BaseDirectory);
                    services.AddSingleton(new RecordingRuntimeOptions(
                        ffmpegPath, recordingSettings.FrameRate, recordingSettings.PreferredEncoder, recordingSettings.FallbackEncoder));

                    // Translation (Milestone 6): company OpenAI-compatible JA→ZH.
                    var translationSettings = context.Configuration.GetSection("Translation").Get<TranslationSettings>() ?? new TranslationSettings();
                    var translationOptions = BuildTranslationOptions(translationSettings);
                    var secretsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "KikuCaption", "secrets");
                    services.AddKikuCaptionTranslation(translationOptions, secretsDir);

                    // UI-R5C: meeting summaries reuse the translation API config + DPAPI key + HttpClient.
                    services.AddKikuCaptionSummarization();
                    services.AddSingleton<KikuCaption.App.Services.MeetingSummaryCoordinator>();
                    services.AddSingleton<MeetingPlaybackCoordinator>();
                    services.AddSingleton<MeetingPlaybackWindowManager>();

                    // Milestone 7: preflight + user settings store (non-sensitive prefs).
                    services.AddSingleton<KikuCaption.App.Services.PreflightService>();
                    services.AddSingleton(_ => UserSettingsStore.CreateDefault());

                    // UI-R1: app-composition environment probes (reuse already-composed options).
                    // These join the Infrastructure probes via IEnumerable<IEnvironmentProbe>.
                    services.AddSingleton<IEnvironmentProbe, WhisperWorkerProbe>();
                    services.AddSingleton<IEnvironmentProbe, WhisperModelProbe>();
                    services.AddSingleton<IEnvironmentProbe, AudioOutputDeviceProbe>();
                    services.AddSingleton<IEnvironmentProbe, OutputDirectoryProbe>();
                    services.AddSingleton<IEnvironmentProbe, TranslationApiProbe>();

                    services.AddSingleton<AudioCaptureViewModel>();
                    services.AddSingleton<SpeechViewModel>();
                    services.AddSingleton<SubtitleOverlayViewModel>();
                    services.AddSingleton<MeetingTimelineViewModel>();
                    services.AddSingleton<TranslationViewModel>();
                    services.AddSingleton<RealtimeCaptionViewModel>();

                    // Localization (UI-R3): the same instance the Loc markup extension uses.
                    services.AddSingleton(KikuCaption.App.Localization.LocalizationService.Instance);

                    // Shell + in-window navigation + pages (UI-R1 shell; UI-R2/R3 pages).
                    services.AddSingleton<HomePageViewModel>();
                    services.AddSingleton<EnvironmentPageViewModel>();
                    services.AddSingleton<AudioPageViewModel>();
                    services.AddSingleton<GeneralSettingsViewModel>();
                    services.AddSingleton<SubtitleSettingsViewModel>();
                    services.AddSingleton<SettingsPageViewModel>();
                    services.AddSingleton<IDictionaryPrompts, KikuCaption.App.Services.DictionaryPrompts>();
                    services.AddSingleton<DictionaryPageViewModel>();
                    services.AddSingleton<ShellViewModel>();
                    services.AddSingleton<INavigationService>(sp =>
                    {
                        var nav = new NavigationService();
                        nav.Register(PageKey.Home, () => sp.GetRequiredService<HomePageViewModel>());
                        nav.Register(PageKey.Environment, () => sp.GetRequiredService<EnvironmentPageViewModel>());
                        nav.Register(PageKey.Audio, () => sp.GetRequiredService<AudioPageViewModel>());
                        nav.Register(PageKey.Settings, () => sp.GetRequiredService<SettingsPageViewModel>());
                        nav.Register(PageKey.Dictionary, () => sp.GetRequiredService<DictionaryPageViewModel>());
                        return nav;
                    });

                    services.AddSingleton<SubtitleOverlayWindow>();
                    services.AddSingleton<MainWindow>();

                    // UI-R5B: shared meeting launcher (home button + tray) and the system-tray coordinator.
                    services.AddSingleton<Func<HomePageViewModel>>(sp => () => sp.GetRequiredService<HomePageViewModel>());
                    services.AddSingleton<KikuCaption.App.Services.IMeetingLauncher, KikuCaption.App.Services.MeetingLauncher>();
                    services.AddSingleton<Func<KikuCaption.App.Services.IMeetingLauncher>>(
                        sp => () => sp.GetRequiredService<KikuCaption.App.Services.IMeetingLauncher>());
                    services.AddSingleton<KikuCaption.App.Tray.ITrayIconAdapter, KikuCaption.App.Tray.WinFormsTrayIconAdapter>();
                    services.AddSingleton<KikuCaption.App.Tray.IMainWindowController>(sp => sp.GetRequiredService<MainWindow>());
                    services.AddSingleton<KikuCaption.App.Tray.ITraySessionSource>(sp =>
                        new KikuCaption.App.Tray.RealtimeTraySessionSource(sp.GetRequiredService<RealtimeCaptionViewModel>()));
                    services.AddSingleton<KikuCaption.App.Tray.ISystemTrayService>(sp =>
                    {
                        var store = sp.GetRequiredService<UserSettingsStore>();
                        var loc = sp.GetRequiredService<KikuCaption.App.Localization.LocalizationService>();
                        return new KikuCaption.App.Tray.SystemTrayService(
                            sp.GetRequiredService<KikuCaption.App.Tray.ITrayIconAdapter>(),
                            sp.GetRequiredService<KikuCaption.App.Tray.IMainWindowController>(),
                            sp.GetRequiredService<KikuCaption.App.Tray.ITraySessionSource>(),
                            sp.GetRequiredService<INavigationService>(),
                            sp.GetRequiredService<KikuCaption.App.Services.IMeetingLauncher>(),
                            loc,
                            minimizeToTray: () => store.Load().Settings.MinimizeToTray,
                            closeToTray: () => store.Load().Settings.CloseToTray,
                            confirmExitWhileRunning: () => MessageBox.Show(
                                loc["Confirm.CloseWhileRecording"], loc["Common.AppName"],
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
                            shutdown: () => Dispatcher.Invoke(() =>
                            {
                                try { sp.GetRequiredService<SubtitleOverlayWindow>().Close(); } catch { /* ignore */ }
                                Shutdown();
                            }),
                            sp.GetRequiredService<ILogger<KikuCaption.App.Tray.SystemTrayService>>());
                    });
                })
                .Build();

            await _host.StartAsync();

            // UI-R3: apply persisted UI preferences (language, subtitle appearance, capture target)
            // before the window is shown.
            SeedUiPreferences(_host.Services);

            // Milestone 7: clean over-retention rolling logs at startup (never touches meeting data).
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            var (userSettings, _) = _host.Services.GetRequiredService<UserSettingsStore>().Load();
            var retentionDays = userSettings.LogRetentionDays > 0
                ? userSettings.LogRetentionDays
                : _host.Services.GetRequiredService<IConfiguration>().GetValue<int?>("Storage:LogRetentionDays") ?? 14;
            LogRetention.CleanupOldLogs(logDir, retentionDays);

            // Force configuration validation to run at startup.
            _ = _host.Services.GetRequiredService<IOptions<KikuCaptionOptions>>().Value;

            Log.Information("KikuCaption starting up (version {Version}).", GetType().Assembly.GetName().Version);

            // Milestone 7 / OKI: overlay persisted translation settings (settings.json + DPAPI
            // endpoint) onto the live options BEFORE the queue starts, so a saved config is used.
            SeedTranslationFromPersisted(_host.Services);

            // Start the translation queue: recovers Pending/RetryScheduled jobs and drains in the
            // background. Safe when translation is disabled (nothing gets enqueued).
            await _host.Services.GetRequiredService<TranslationQueue>().StartAsync(CancellationToken.None);

            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Show();
            // The subtitle overlay window is constructed first (a MainWindow ctor dependency), so WPF
            // auto-assigns it to Application.MainWindow. Point MainWindow at the real shell so dialogs
            // owned by "the main window" center on it and are modal to it (not the hidden overlay).
            MainWindow = window;

            // UI-R5B: attach + start the single system-tray coordinator after the window is shown.
            var tray = _host.Services.GetRequiredService<KikuCaption.App.Tray.ISystemTrayService>();
            window.AttachTray(tray);
            tray.Start();
        }
        catch (OptionsValidationException ex)
        {
            ShowFatal("配置文件校验失败：\n" + string.Join("\n", ex.Failures));
        }
        catch (Exception ex)
        {
            ShowFatal("KikuCaption 启动失败：" + ex.Message);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // UI-R5B: dispose the tray icon first so no ghost remains, on every exit path (idempotent
            // if the explicit-exit flow already disposed it).
            try { _host.Services.GetRequiredService<KikuCaption.App.Tray.ISystemTrayService>().Dispose(); } catch { /* ignore */ }

            // Stop the translation queue first: cancels the in-flight request but leaves Pending
            // jobs durable in SQLite for the next run.
            try { await _host.Services.GetRequiredService<TranslationQueue>().DisposeAsync(); } catch { /* ignore */ }
            await _host.StopAsync();
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void ShowFatal(string message)
    {
        MessageBox.Show(message, "KikuCaption", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }

    private static string AppVersion()
        => typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    // Applies previously-saved translation settings (non-secret fields from settings.json, and the
    // DPAPI-encrypted endpoint) onto the live TranslationOptions singleton, so "save once → read next
    // time" works. Absent/failed items keep the appsettings defaults.
    private static void SeedTranslationFromPersisted(IServiceProvider sp)
    {
        var opts = sp.GetRequiredService<TranslationOptions>();
        var store = sp.GetRequiredService<UserSettingsStore>();
        var secrets = sp.GetRequiredService<KikuCaption.Translation.Security.ITranslationSecretStore>();

        if (File.Exists(store.FilePath))
        {
            var (us, _) = store.Load();
            opts.Enabled = us.TranslationEnabled;
            if (!string.IsNullOrWhiteSpace(us.TranslationModel)) opts.Model = us.TranslationModel;
            if (!string.IsNullOrWhiteSpace(us.TranslationApiVersion)) opts.ApiVersion = us.TranslationApiVersion;
            if (Enum.TryParse<TranslationAuthMode>(us.TranslationAuthMode, ignoreCase: true, out var m)) opts.AuthenticationMode = m;
            if (!string.IsNullOrWhiteSpace(us.TranslationHeaderName)) opts.HeaderName = us.TranslationHeaderName;
            opts.Proxy = us.TranslationProxy ?? "";
            if (!string.IsNullOrWhiteSpace(us.TranslationTargetLanguage)) opts.TargetLanguage = us.TranslationTargetLanguage;
            opts.TimeoutSeconds = Math.Clamp(us.TranslationTimeoutSeconds, 1, 300);
            opts.MaxRetries = Math.Clamp(us.TranslationMaxRetries, 0, 10);
        }

        try
        {
            var ep = secrets.ReadEndpoint();
            if (!string.IsNullOrWhiteSpace(ep)) opts.Endpoint = ep;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the saved translation endpoint (DPAPI); keeping default.");
        }
    }

    // UI-R3: applies persisted, non-sensitive UI preferences before the window shows — the UI
    // language, subtitle appearance (onto the live overlay), the default recognition language and
    // the remembered capture target (onto the meeting view model). Never reads the API key.
    private static void SeedUiPreferences(IServiceProvider sp)
    {
        var store = sp.GetRequiredService<UserSettingsStore>();
        if (!File.Exists(store.FilePath))
        {
            return;
        }

        var (us, _) = store.Load();

        var localization = sp.GetRequiredService<KikuCaption.App.Localization.LocalizationService>();
        // A corrupt/unsupported persisted code normalizes to zh-CN (UI-R3.1 unified fallback).
        localization.SetLanguage(KikuCaption.App.Localization.LocalizationService.NormalizeCulture(us.UiLanguage));

        sp.GetRequiredService<SubtitleOverlayViewModel>().ApplyAppearance(us);

        var realtime = sp.GetRequiredService<RealtimeCaptionViewModel>();
        if (!string.IsNullOrWhiteSpace(us.RecognitionLanguage))
        {
            realtime.SelectedLanguage = us.RecognitionLanguage;
        }
        realtime.ApplyCaptureTarget(new KikuCaption.App.ViewModels.MeetingCaptureTarget(
            string.IsNullOrWhiteSpace(us.CaptureType) ? "screen" : us.CaptureType,
            us.CaptureTarget));

        // UI-R5A: restore the meeting audio inputs (system audio / microphone / stable device id).
        realtime.ApplyAudioOptions(new KikuCaption.App.ViewModels.MeetingAudioOptions(
            us.RecordSystemAudio, us.RecordMicrophone, us.MicrophoneDeviceId));
    }

    private static TranslationOptions BuildTranslationOptions(TranslationSettings s)
    {
        var mode = Enum.TryParse<TranslationAuthMode>(s.AuthenticationMode, ignoreCase: true, out var m)
            ? m
            : TranslationAuthMode.Bearer;

        return new TranslationOptions
        {
            Enabled = s.Enabled,
            Endpoint = s.Endpoint,
            Model = s.Model,
            ApiVersion = s.ApiVersion,
            AuthenticationMode = mode,
            HeaderName = string.IsNullOrWhiteSpace(s.HeaderName) ? "Authorization" : s.HeaderName,
            Proxy = s.Proxy ?? "",
            TimeoutSeconds = s.TimeoutSeconds,
            MaxRetries = s.MaxRetries,
            MaxQueueLength = s.MaxQueueLength,
            MaxConcurrency = s.MaxConcurrency,
            MaxInputCharacters = s.MaxInputCharacters,
            SourceLanguage = string.IsNullOrWhiteSpace(s.SourceLanguage) ? "ja" : s.SourceLanguage,
            TargetLanguage = string.IsNullOrWhiteSpace(s.TargetLanguage) ? "zh" : s.TargetLanguage
        };
    }

    private static WhisperWorkerOptions BuildWhisperWorkerOptions(SpeechSettings speech)
    {
        string? python = speech.PythonExecutable;
        string? script = speech.WorkerScript;

        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
        {
            var located = WhisperWorkerLocator.TryLocate(AppContext.BaseDirectory);
            if (located is not null)
            {
                python = string.IsNullOrWhiteSpace(python) ? located.Value.PythonExecutable : python;
                script = string.IsNullOrWhiteSpace(script) ? located.Value.WorkerScript : script;
            }
        }

        string? modelDir = speech.ModelCacheDirectory;
        if (string.IsNullOrWhiteSpace(modelDir) && !string.IsNullOrWhiteSpace(script))
        {
            var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)));
            if (repoRoot is not null)
            {
                modelDir = Path.Combine(repoRoot, "models", "whisper");
            }
        }

        return new WhisperWorkerOptions
        {
            PythonExecutable = string.IsNullOrWhiteSpace(python) ? "python" : python!,
            WorkerScript = script ?? string.Empty,
            ModelCacheDirectory = modelDir
        };
    }
}
