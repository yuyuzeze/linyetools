namespace KikuCaption.Translation;

/// <summary>Authentication modes for the company API (M6). The secret itself is never here.</summary>
public enum TranslationAuthMode
{
    /// <summary><c>Authorization: Bearer &lt;secret&gt;</c>.</summary>
    Bearer,

    /// <summary>A configurable header (e.g. <c>api-key: &lt;secret&gt;</c>).</summary>
    ApiKeyHeader,

    /// <summary>No client auth (a company gateway already handles it, or controlled tests).</summary>
    None
}

/// <summary>
/// Fully-configurable settings for the company OpenAI-compatible translation API (M6 §config).
/// No Microsoft domain, deployment URL, or API version is hard-coded; <see cref="Endpoint"/> is the
/// complete request address. The API key is NOT stored here — it lives only in the DPAPI secret store.
/// </summary>
public sealed class TranslationOptions
{
    // Live-editable from the settings panel (read per request by the adapter/trigger).
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public TranslationAuthMode AuthenticationMode { get; set; } = TranslationAuthMode.Bearer;
    public string HeaderName { get; set; } = "Authorization";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int MaxInputCharacters { get; set; } = 4000;
    public string SourceLanguage { get; set; } = "ja";
    public string TargetLanguage { get; set; } = "zh";

    // Read once at queue construction (channel size / worker count); a change needs a restart.
    public int MaxQueueLength { get; init; } = 100;
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Hard cap on the response we will buffer, to reject unbounded bodies.</summary>
    public long MaxResponseBytes { get; init; } = 512 * 1024;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 300));

    /// <summary>Clamps concurrency to a sane desktop range (order-preserving default is 1).</summary>
    public int EffectiveConcurrency => Math.Clamp(MaxConcurrency, 1, 8);

    public int EffectiveQueueLength => Math.Clamp(MaxQueueLength, 1, 100_000);

    public int EffectiveMaxRetries => Math.Clamp(MaxRetries, 0, 10);
}
