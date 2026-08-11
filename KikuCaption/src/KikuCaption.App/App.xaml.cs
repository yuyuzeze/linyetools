using System.IO;
using System.Windows;
using KikuCaption.App.ViewModels;
using KikuCaption.App.Views;
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

                    services.AddSingleton<ISpeechOptionsProvider>(new SpeechOptionsProvider(baseSpeechOptions, contexts));

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

                    // Milestone 7: preflight + user settings store (non-sensitive prefs).
                    services.AddSingleton<KikuCaption.App.Services.PreflightService>();
                    services.AddSingleton(_ => UserSettingsStore.CreateDefault());

                    services.AddSingleton<AudioCaptureViewModel>();
                    services.AddSingleton<SpeechViewModel>();
                    services.AddSingleton<SubtitleOverlayViewModel>();
                    services.AddSingleton<MeetingTimelineViewModel>();
                    services.AddSingleton<TranslationViewModel>();
                    services.AddSingleton<RealtimeCaptionViewModel>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<SubtitleOverlayWindow>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            // Milestone 7: clean over-retention rolling logs at startup (never touches meeting data).
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            var retentionDays = _host.Services.GetRequiredService<IConfiguration>().GetValue<int?>("Storage:LogRetentionDays") ?? 14;
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
