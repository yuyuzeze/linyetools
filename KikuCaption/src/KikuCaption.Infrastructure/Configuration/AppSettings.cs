namespace KikuCaption.Infrastructure.Configuration;

/// <summary>
/// Strongly-typed view of <c>appsettings.json</c> (PROJECT.md 11).
/// Bound at startup and validated by <see cref="KikuCaptionOptionsValidator"/>.
/// Secrets are never stored here (PROJECT.md 5.6, 13).
/// </summary>
public sealed class KikuCaptionOptions
{
    public SpeechSettings Speech { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public SubtitleSettings Subtitle { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
}

public sealed class SpeechSettings
{
    public string Engine { get; set; } = "Whisper";
    public string Model { get; set; } = "small";
    public string ComputeType { get; set; } = "int8";
    public string Language { get; set; } = "ja";
    public int BeamSize { get; set; } = 2;
    public double WindowSeconds { get; set; } = 6;
    public double OverlapSeconds { get; set; } = 2;

    // Progressive-captioning tunables (now actually mapped through to the pipeline).
    public int SilenceFinalMs { get; set; } = 1000;
    public int StableRepeatCount { get; set; } = 3;
    public double MaxSentenceSeconds { get; set; } = 12;
    public double MaxWaitSeconds { get; set; } = 20;

    /// <summary>
    /// Per-language decoding context. Keyed by language ("ja" / "zh"); each provides an initial
    /// prompt and an editable technical-term glossary. A language only ever gets its own context —
    /// choosing zh never sends the Japanese prompt. Not company-sensitive.
    /// </summary>
    public Dictionary<string, SpeechContextSettings> Contexts { get; set; } = new();

    // Worker location (Milestone 2). Empty = auto-detect the repo's python/whisper_worker.
    public string? PythonExecutable { get; set; }
    public string? WorkerScript { get; set; }

    /// <summary>Explicit, discoverable model cache directory. Empty = &lt;repo&gt;/models/whisper.</summary>
    public string? ModelCacheDirectory { get; set; }
}

public sealed class TranslationSettings
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiVersion { get; set; } = "";

    /// <summary>Bearer | ApiKeyHeader | None.</summary>
    public string AuthenticationMode { get; set; } = "Bearer";
    public string HeaderName { get; set; } = "Authorization";

    /// <summary>Optional corporate proxy (e.g. http://proxy.host:8080). Empty = system proxy.</summary>
    public string Proxy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int MaxQueueLength { get; set; } = 100;
    public int MaxConcurrency { get; set; } = 1;
    public int MaxInputCharacters { get; set; } = 4000;
    public string SourceLanguage { get; set; } = "ja";
    public string TargetLanguage { get; set; } = "zh";
    // NOTE: ApiKey is NEVER stored here — only in the DPAPI secret store (PROJECT.md 5.6, M6 §8).
}

/// <summary>One language's recognition decoding context (bound from <c>Speech:Contexts:&lt;lang&gt;</c>).</summary>
public sealed class SpeechContextSettings
{
    public string InitialPrompt { get; set; } = "";
    public string[] Hotwords { get; set; } = System.Array.Empty<string>();
}

public sealed class RecordingSettings
{
    public int FrameRate { get; set; } = 15;
    public string PreferredEncoder { get; set; } = "h264_qsv";
    public string FallbackEncoder { get; set; } = "libx264";
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>Explicit ffmpeg.exe path (Milestone 5). Empty = auto-locate (tools/ffmpeg, then PATH).</summary>
    public string? FFmpegPath { get; set; }
}

public sealed class SubtitleSettings
{
    public double FontSize { get; set; } = 26;
    public double Opacity { get; set; } = 0.85;
    public int MaxLines { get; set; } = 4;
    public bool ClickThrough { get; set; }
}

public sealed class StorageSettings
{
    public string OutputDirectory { get; set; } = "Meetings";
    public double MinimumFreeSpaceGb { get; set; } = 2;
    public int LogRetentionDays { get; set; } = 14;
}
