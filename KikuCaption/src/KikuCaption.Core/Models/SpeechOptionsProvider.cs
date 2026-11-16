using KikuCaption.Core.Enums;
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
    private readonly IWhisperModelLocator? _modelLocator;
    private readonly ISpeechEngineSelection? _engineSelection;

    /// <summary>Fixed per-language context map (used by tests and static configurations).</summary>
    public SpeechOptionsProvider(SpeechOptions baseOptions, IReadOnlyDictionary<string, SpeechContext>? contexts = null,
        IWhisperModelLocator? modelLocator = null, ISpeechEngineSelection? engineSelection = null)
    {
        _base = baseOptions;
        _contexts = contexts ?? new Dictionary<string, SpeechContext>();
        _modelLocator = modelLocator;
        _engineSelection = engineSelection;
    }

    /// <summary>
    /// Resolves the context from the active dictionary in <paramref name="store"/> at call time.
    /// A missing/unsupported language yields no context (base options only).
    /// </summary>
    public SpeechOptionsProvider(SpeechOptions baseOptions, ISpeechDictionaryStore store,
        IWhisperModelLocator? modelLocator = null, ISpeechEngineSelection? engineSelection = null)
    {
        _base = baseOptions;
        _store = store;
        _contexts = new Dictionary<string, SpeechContext>();
        _modelLocator = modelLocator;
        _engineSelection = engineSelection;
    }

    /// <summary>speech.cpp models we ship are Japanese-only (Haas); zh always stays on Whisper.</summary>
    private static bool SpeechCppSupportsLanguage(string language) =>
        string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase);

    public SpeechOptions ForLanguage(string language) => ForLanguage(language, WhisperModelPurpose.Realtime);

    public SpeechOptions ForLanguage(string language, WhisperModelPurpose purpose)
    {
        // Start from the shared base, cleared of any context, then apply only THIS language's context.
        var options = _base with { Language = language, InitialPrompt = null, Hotwords = null };

        // speech.cpp engine: only for live/file/prewarm (never post-meeting correction, which stays
        // on Whisper medium), and only for a language we ship a model for. A speech.cpp session takes
        // no Whisper prompt/hotwords; it uses per-segment timestamps for file recognition and none
        // for real-time (the pipeline supplies timing). Availability is already folded into the
        // selection, so Engine == SpeechCpp here means the backend is actually usable.
        if (_engineSelection is { Engine: SpeechEngine.SpeechCpp } selection
            && !string.IsNullOrWhiteSpace(selection.SpeechCppModel)
            && SpeechCppSupportsLanguage(language)
            && purpose is WhisperModelPurpose.Realtime or WhisperModelPurpose.WavRecognition or WhisperModelPurpose.Prewarm)
        {
            return options with
            {
                Engine = SpeechEngine.SpeechCpp,
                Model = selection.SpeechCppModel!,
                InitialPrompt = null,
                Hotwords = null,
                Keywords = null, // reserved: Haas keyword readings are not derived from the glossary yet
                RequestTimestamps = purpose == WhisperModelPurpose.WavRecognition
            };
        }

        // R7B.1: resolve the model through the single locator (session-start snapshot). A managed
        // install yields its ABSOLUTE directory; otherwise the legacy model name is kept.
        if (_modelLocator is not null)
        {
            options = options with { Model = _modelLocator.Resolve(_base.Model, purpose).ModelName };
        }

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
