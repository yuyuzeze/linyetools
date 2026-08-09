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

    public bool TranslationEnabled { get; init; }
    public string TranslationEndpoint { get; init; } = "";
    public string TranslationModel { get; init; } = "";
    public string TranslationApiVersion { get; init; } = "";
    public string TranslationAuthMode { get; init; } = "Bearer";
    public string TranslationHeaderName { get; init; } = "Authorization";

    public double SubtitleFontSize { get; init; } = 26;
    public double SubtitleOpacity { get; init; } = 0.85;
    public double TimelineWidth { get; init; } = 430;
    public bool ClickThrough { get; init; }
    public bool AutoScroll { get; init; } = true;
}
