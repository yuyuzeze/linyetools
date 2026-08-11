namespace KikuCaption.Core.Models;

/// <summary>
/// Recognition configuration passed to <see cref="Interfaces.ISpeechRecognizer.InitializeAsync"/>
/// (PROJECT.md 5.4, 8.3). Defaults match the project baseline: small / cpu / int8 / beam_size 1.
/// </summary>
public sealed record SpeechOptions
{
    public string Model { get; init; } = "small";
    public string Device { get; init; } = "cpu";
    public string ComputeType { get; init; } = "int8";
    public int BeamSize { get; init; } = 1;

    /// <summary>Recognition language chosen by the user: "ja" or "zh".</summary>
    public required string Language { get; init; }

    /// <summary>Explicit, discoverable model cache directory. Null lets the worker use its default.</summary>
    public string? ModelCacheDirectory { get; init; }

    /// <summary>
    /// Optional decoding context passed to faster-whisper's <c>initial_prompt</c> — a short hint of
    /// domain/style (e.g. meeting jargon). Null/empty = none.
    /// </summary>
    public string? InitialPrompt { get; init; }

    /// <summary>
    /// Optional glossary of technical terms passed to faster-whisper's <c>hotwords</c> (biases the
    /// decoder toward these). Kept as a structured list on the wire; the worker joins it. Validate
    /// with <see cref="Hotwords"/> before use to bound count/length.
    /// </summary>
    public IReadOnlyList<string>? Hotwords { get; init; }

    /// <summary>How long to wait for the worker to load the model and report ready.</summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromMinutes(3);
}
