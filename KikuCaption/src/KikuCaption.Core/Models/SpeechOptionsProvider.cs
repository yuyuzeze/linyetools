using KikuCaption.Core.Interfaces;

namespace KikuCaption.Core.Models;

/// <summary>Per-language decoding context: an initial prompt and a technical-term glossary.</summary>
public sealed record SpeechContext(string? InitialPrompt, IReadOnlyList<string>? Hotwords);

/// <summary>
/// Default <see cref="ISpeechOptionsProvider"/>. Holds one set of base options (model / device /
/// compute / beam / cache) and a per-language <see cref="SpeechContext"/> map. <see cref="ForLanguage"/>
/// returns the base options with the requested language and ONLY that language's prompt/hotwords —
/// so choosing <c>zh</c> never sends the Japanese prompt (and vice versa). Pure and testable.
/// </summary>
public sealed class SpeechOptionsProvider : ISpeechOptionsProvider
{
    private readonly SpeechOptions _base;
    private readonly IReadOnlyDictionary<string, SpeechContext> _contexts;

    public SpeechOptionsProvider(SpeechOptions baseOptions, IReadOnlyDictionary<string, SpeechContext>? contexts = null)
    {
        _base = baseOptions;
        _contexts = contexts ?? new Dictionary<string, SpeechContext>();
    }

    public SpeechOptions ForLanguage(string language)
    {
        // Start from the shared base, cleared of any context, then apply only THIS language's context.
        var options = _base with { Language = language, InitialPrompt = null, Hotwords = null };

        if (_contexts.TryGetValue(language, out var context))
        {
            options = options with
            {
                InitialPrompt = string.IsNullOrWhiteSpace(context.InitialPrompt) ? null : context.InitialPrompt,
                Hotwords = context.Hotwords is { Count: > 0 } ? context.Hotwords : null
            };
        }

        return options;
    }
}
