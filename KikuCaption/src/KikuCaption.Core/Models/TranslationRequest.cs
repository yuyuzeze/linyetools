namespace KikuCaption.Core.Models;

/// <summary>
/// A single translation request carrying everything the adapter needs, all from the job's immutable
/// session snapshot (UI-R4A fix): the source/target direction, the model, and the prompt version.
/// Nothing here is read from live settings, so a mid-session settings change or a crash-recovered job
/// always translates exactly as it was enqueued.
/// </summary>
public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    string Model,
    int PromptVersion);
