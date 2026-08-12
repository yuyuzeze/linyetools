using KikuCaption.Core.Interfaces;

namespace KikuCaption.Core.Models;

/// <summary>Per-language decoding context: an initial prompt and a technical-term glossary.</summary>
public sealed record SpeechContext(string? InitialPrompt, IReadOnlyList<string>? Hotwords);

/// <summary>
/// Default <see cref="ISpeechOptionsProvider"/>. Holds one set of base options (model / device /
/// compute / beam / cache) and resolves the per-language decoding context — either from a fixed
/// <see cref="SpeechContext"/> map or, when wired with an <see cref="ISpeechDictionaryStore"/>, from
/// the language's currently-active dictionary. <see cref="ForLanguage"/> returns the base options
/// with the requested language and ONLY that language's prompt/hotwords — so choosing <c>zh</c>
/// never sends the Japanese prompt (and vice versa).
///
/// The context is snapshotted at the moment <see cref="ForLanguage"/> is called (once per session
/// start): the returned <see cref="SpeechOptions"/> is immutable, so a dictionary switch mid-meeting
/// cannot alter an already-running session — the next meeting reads the new active dictionary. Pure
/// and testable; the store path never mutates state and the returned collections are copies.
/// </summary>
public sealed class SpeechOptionsProvider : ISpeechOptionsProvider
{
    private readonly SpeechOptions _base;
    private readonly IReadOnlyDictionary<string, SpeechContext> _contexts;
    private readonly ISpeechDictionaryStore? _store;

    /// <summary>Fixed per-language context map (used by tests and static configurations).</summary>
    public SpeechOptionsProvider(SpeechOptions baseOptions, IReadOnlyDictionary<string, SpeechContext>? contexts = null)
    {
        _base = baseOptions;
        _contexts = contexts ?? new Dictionary<string, SpeechContext>();
    }

    /// <summary>
    /// Resolves the context from the active dictionary in <paramref name="store"/> at call time.
    /// A missing/unsupported language yields no context (base options only).
    /// </summary>
    public SpeechOptionsProvider(SpeechOptions baseOptions, ISpeechDictionaryStore store)
    {
        _base = baseOptions;
        _store = store;
        _contexts = new Dictionary<string, SpeechContext>();
    }

    public SpeechOptions ForLanguage(string language)
    {
        // Start from the shared base, cleared of any context, then apply only THIS language's context.
        var options = _base with { Language = language, InitialPrompt = null, Hotwords = null };

        var context = ResolveContext(language);
        if (context is not null)
        {
            options = options with
            {
                InitialPrompt = string.IsNullOrWhiteSpace(context.InitialPrompt) ? null : context.InitialPrompt,
                // Copy the list so later store/UI edits can never mutate this session's snapshot.
                Hotwords = context.Hotwords is { Count: > 0 } ? context.Hotwords.ToArray() : null
            };
        }

        return options;
    }

    private SpeechContext? ResolveContext(string language)
    {
        if (_store is not null)
        {
            // Only supported recognition languages have a dictionary; others get no context.
            if (!SpeechDictionaryProfile.IsSupportedLanguage(language))
            {
                return null;
            }

            var active = _store.GetActiveProfile(language);
            return new SpeechContext(active.InitialPrompt, active.Hotwords);
        }

        return _contexts.TryGetValue(language, out var ctx) ? ctx : null;
    }
}
