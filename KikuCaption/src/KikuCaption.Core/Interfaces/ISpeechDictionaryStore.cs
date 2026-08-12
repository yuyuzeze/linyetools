using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Persists the user's speech-recognition dictionaries and the one active dictionary per language.
/// The concrete store lives outside Core (it owns file IO); Core only depends on this contract so
/// <see cref="ISpeechOptionsProvider"/> can resolve the active decoding context without knowing how
/// or where it is stored.
///
/// Every returned <see cref="SpeechDictionaryProfile"/> (and list) is an immutable snapshot/copy —
/// callers can never mutate the store's state by holding a reference, and a profile captured at
/// meeting start is unaffected by later edits. Implementations must be safe for concurrent access.
/// The full prompt/hotwords are never logged (only ProfileId / language / hotword count).
/// </summary>
public interface ISpeechDictionaryStore
{
    /// <summary>All profiles, optionally filtered to one recognition language. Snapshot copies.</summary>
    IReadOnlyList<SpeechDictionaryProfile> GetProfiles(string? languageCode = null);

    /// <summary>The profile with this id, or null. Snapshot copy.</summary>
    SpeechDictionaryProfile? GetById(Guid id);

    /// <summary>The active profile id for a language (falls back to that language's built-in).</summary>
    Guid GetActiveId(string languageCode);

    /// <summary>
    /// The active profile for a language, never null — if no active is set or it was deleted, the
    /// language's built-in default is returned. This is what <see cref="ISpeechOptionsProvider"/>
    /// snapshots at meeting start.
    /// </summary>
    SpeechDictionaryProfile GetActiveProfile(string languageCode);

    /// <summary>
    /// Creates (unknown id) or updates (existing user id) a profile. Validates via
    /// <see cref="SpeechDictionaryProfile.Normalized"/> and enforces per-language name uniqueness.
    /// Built-in profiles cannot be modified. Returns the stored copy (with timestamps applied).
    /// </summary>
    SpeechDictionaryProfile Upsert(SpeechDictionaryProfile profile);

    /// <summary>Sets the active profile for a language. The profile must exist and match the language.</summary>
    void SetActive(string languageCode, Guid id);

    /// <summary>
    /// Deletes a user profile. Built-in profiles cannot be deleted. If the deleted profile was the
    /// active one for its language, the active selection falls back to that language's built-in — the
    /// delete and the active-mapping change are persisted in one atomic write.
    /// </summary>
    void Delete(Guid id);

    /// <summary>
    /// Restores the two built-in profiles to their seeded (appsettings) content, in case a persisted
    /// copy drifted. User profiles and active selections are untouched.
    /// </summary>
    void RestoreBuiltInDefaults();
}
