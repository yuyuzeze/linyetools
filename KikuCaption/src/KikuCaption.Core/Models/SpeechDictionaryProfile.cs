namespace KikuCaption.Core.Models;

/// <summary>
/// A named recognition-context dictionary for the local faster-whisper worker: an initial prompt
/// and a technical-term glossary (hotwords) scoped to ONE recognition language ("ja" or "zh").
/// Users keep several of these and mark one active per language. Built-in profiles are seeded from
/// <c>appsettings</c> and are read-only (they can be viewed and copied, never edited or deleted).
///
/// This is a pure Core value type — it never touches the file system, the SQLite store, or the
/// translation API. A dictionary only ever feeds the local speech worker's <c>initial_prompt</c> and
/// <c>hotwords</c>; it is NOT an API key and is NEVER sent to the translation service.
/// </summary>
public sealed record SpeechDictionaryProfile
{
    /// <summary>Only these two recognition languages are supported (no English recognition).</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = new[] { "ja", "zh" };

    /// <summary>Stable id for the built-in Japanese dictionary (seeded from appsettings, idempotent).</summary>
    public static readonly Guid BuiltInJapaneseId = new("a1b2c3d4-0000-4000-8000-00000000000a");

    /// <summary>Stable id for the built-in Chinese dictionary (seeded from appsettings, idempotent).</summary>
    public static readonly Guid BuiltInChineseId = new("a1b2c3d4-0000-4000-8000-00000000000b");

    public const int MaxNameLength = 60;

    /// <summary>
    /// Explicit upper bound for the initial prompt. The C#↔Python protocol has no dedicated
    /// prompt-size cap, so we bound it here (front and back use the same limit): 2000 characters is
    /// far larger than any real meeting hint yet keeps a single JSON-Lines message small.
    /// </summary>
    public const int MaxInitialPromptLength = 2000;

    /// <summary>Stable identity. Never changes once created (survives rename/edit).</summary>
    public required Guid Id { get; init; }

    /// <summary>User-facing name. Never auto-translated. Unique per language (case/trim-insensitive).</summary>
    public required string Name { get; init; }

    /// <summary>Recognition language this dictionary applies to: "ja" or "zh".</summary>
    public required string LanguageCode { get; init; }

    /// <summary>Optional faster-whisper <c>initial_prompt</c>. Null/empty = none.</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>Normalized technical-term glossary (see <see cref="Hotwords"/> for the limits).</summary>
    public IReadOnlyList<string> Hotwords { get; init; } = Array.Empty<string>();

    /// <summary>True for the two seeded defaults: read-only, never deletable.</summary>
    public bool IsBuiltIn { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>True if <paramref name="language"/> is a supported recognition language.</summary>
    public static bool IsSupportedLanguage(string? language)
        => language is not null && SupportedLanguages.Contains(language, StringComparer.Ordinal);

    /// <summary>The stable built-in id for a language, or null if the language is unsupported.</summary>
    public static Guid? BuiltInIdFor(string language) => language switch
    {
        "ja" => BuiltInJapaneseId,
        "zh" => BuiltInChineseId,
        _ => null
    };

    /// <summary>
    /// Validates and normalizes this profile for persistence: trims/bounds the name, validates the
    /// language, bounds the initial prompt, and runs the shared <see cref="Models.Hotwords"/> rules
    /// (count/length/total/dedup). Throws <see cref="ArgumentException"/> on any violation so the
    /// front (UI) and back (store) reject identically. Returns a new, cleaned profile.
    /// </summary>
    public SpeechDictionaryProfile Normalized()
    {
        var name = (Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("词典名称不能为空。", nameof(Name));
        }

        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"词典名称长度 {name.Length} 超过上限 {MaxNameLength}。", nameof(Name));
        }

        if (!IsSupportedLanguage(LanguageCode))
        {
            throw new ArgumentException($"不支持的识别语言：{LanguageCode}。", nameof(LanguageCode));
        }

        var prompt = string.IsNullOrWhiteSpace(InitialPrompt) ? null : InitialPrompt.Trim();
        if (prompt is not null && prompt.Length > MaxInitialPromptLength)
        {
            throw new ArgumentException($"初始提示长度 {prompt.Length} 超过上限 {MaxInitialPromptLength}。", nameof(InitialPrompt));
        }

        var hotwords = Models.Hotwords.Normalize(Hotwords);

        return this with
        {
            Name = name,
            LanguageCode = LanguageCode,
            InitialPrompt = prompt,
            Hotwords = hotwords
        };
    }
}
