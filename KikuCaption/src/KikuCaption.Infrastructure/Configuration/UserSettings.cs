namespace KikuCaption.Infrastructure.Configuration;

/// <summary>
/// User-adjustable, <b>non-sensitive</b> preferences persisted between runs (Milestone 7 §3). The
/// translation API key is intentionally NOT part of this type — it lives only in the DPAPI secret
/// store. Corrupt files fall back to these defaults (see <see cref="UserSettingsStore"/>).
/// </summary>
public sealed record UserSettings
{
    public string? OutputDirectory { get; init; }
    public string RecognitionLanguage { get; init; } = "ja";
    public string? WhisperModelPath { get; init; }
    public string? PythonPath { get; init; }
    public string? FFmpegPath { get; init; }
    public string CaptureType { get; init; } = "screen";
    public string? CaptureTarget { get; init; }
    public int FrameRate { get; init; } = 15;
    public string PreferredEncoder { get; init; } = "h264_qsv";

    // UI-R5A meeting audio inputs (non-secret). Defaults: system audio on, microphone on, default
    // communications input device (null id). MicrophoneDeviceId is a stable WASAPI endpoint id.
    public bool RecordSystemAudio { get; init; } = true;
    public bool RecordMicrophone { get; init; } = true;
    public string? MicrophoneDeviceId { get; init; }

    // UI-R5B system tray. MinimizeToTray hides the window to the notification area on minimize;
    // CloseToTray makes the window's X hide to tray instead of exiting. The tray "Exit" always exits.
    public bool MinimizeToTray { get; init; } = true;
    public bool CloseToTray { get; init; }

    // UI-R5C meeting-summary output language. Null = never chosen (follow the UI language); once the
    // user picks one in the dialog it is persisted here (zh/ja/en) and no longer follows the UI.
    public string? SummaryOutputLanguage { get; init; }

    // Post-meeting high-accuracy pass. Enabled by default to preserve the behaviour introduced
    // with corrected-transcript.*; disabling it leaves the realtime transcript untouched.
    public bool AutoCorrectAfterMeeting { get; init; } = true;

    // Optional startup optimization. False by default because the resident model/worker keeps
    // several hundred MB of memory allocated while no meeting is running.
    public bool PrewarmWhisperInBackground { get; init; }

    public bool TranslationEnabled { get; init; }

    // NOTE: the Endpoint is intentionally NOT persisted here — it can embed a credential (e.g. a
    // function key in the URL), so it is stored DPAPI-encrypted via ITranslationSecretStore instead.
    public string TranslationModel { get; init; } = "";
    public string TranslationApiVersion { get; init; } = "";
    public string TranslationAuthMode { get; init; } = "Bearer";
    public string TranslationHeaderName { get; init; } = "Authorization";
    public string TranslationProxy { get; init; } = "";

    /// <summary>UI-R4A: configurable translation target language (stable code zh/en/ja).</summary>
    public string TranslationTargetLanguage { get; init; } = "zh";

    /// <summary>UI-R4A: request timeout (1–300 s) and max retries (0–10) for the translation API.</summary>
    public int TranslationTimeoutSeconds { get; init; } = 30;
    public int TranslationMaxRetries { get; init; } = 3;

    public double SubtitleFontSize { get; init; } = 26;
    public double SubtitleOpacity { get; init; } = 0.85;
    public double TimelineWidth { get; init; } = 430;
    public bool ClickThrough { get; init; }
    public bool AutoScroll { get; init; } = true;

    // UI-R3 general preferences.
    /// <summary>UI culture: "zh-CN" or "en-US". Internal language codes (ja/zh/en) are unaffected.</summary>
    public string UiLanguage { get; init; } = "zh-CN";
    public bool LoadRecentOnStartup { get; init; }
    public int LogRetentionDays { get; init; } = 14;

    // UI-R3 subtitle appearance (applied live to the overlay; persisted on explicit save).
    public bool DefaultShowOverlay { get; init; }
    public string SubtitleFontFamily { get; init; } = "Segoe UI, Microsoft YaHei UI";
    public int SubtitleMaxLines { get; init; } = 4;
    public bool SubtitleTopmost { get; init; } = true;
    public bool SubtitleShowOriginal { get; init; } = true;
    public bool SubtitleShowTranslation { get; init; } = true;
    public string SubtitleOriginalColor { get; init; } = "#F5F5F5";
    public string SubtitleTranslationColor { get; init; } = "#6FC3FF";
    public double SubtitlePartialOpacity { get; init; } = 0.6;

    /// <summary>Hidden subtitle theme unlocked from the brand: default, night-sakura or deep-sea.</summary>
    public string SubtitleTheme { get; init; } = "default";
}
