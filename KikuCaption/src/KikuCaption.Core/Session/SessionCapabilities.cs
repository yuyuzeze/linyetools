namespace KikuCaption.Core.Session;

/// <summary>
/// An immutable snapshot of what a single meeting will do, computed ONCE when the meeting starts
/// (UI-R6A §八). Downstream modules read this snapshot instead of re-reading UserSettings, so a mid-
/// meeting settings change cannot alter the running session, and the "recording-only" mode is decided
/// in exactly one place.
///
/// <para>Dependencies between capabilities are enforced here: translation and post-meeting correction
/// both require speech recognition (there are no captions to translate or re-transcribe without it),
/// so they are false whenever <see cref="SpeechRecognitionEnabled"/> is false.</para>
/// </summary>
public sealed record SessionCapabilities(
    bool RecordingEnabled,
    bool SpeechRecognitionEnabled,
    bool TranslationEnabled,
    bool PostMeetingCorrectionEnabled)
{
    /// <summary>
    /// Builds the snapshot from the raw facts. <paramref name="speechRecognitionEnabled"/> is the
    /// user's "enable local speech recognition" setting; the other two are gated by it.
    /// </summary>
    public static SessionCapabilities Compute(
        bool recordingAvailable,
        bool speechRecognitionEnabled,
        bool translationEffective,
        bool postMeetingCorrectionRequested)
        => new(
            RecordingEnabled: recordingAvailable,
            SpeechRecognitionEnabled: speechRecognitionEnabled,
            TranslationEnabled: speechRecognitionEnabled && translationEffective,
            PostMeetingCorrectionEnabled: speechRecognitionEnabled && postMeetingCorrectionRequested);

    /// <summary>A meeting that only records screen + audio (no captions/translation/correction).</summary>
    public bool IsRecordingOnly => RecordingEnabled && !SpeechRecognitionEnabled;

    /// <summary>True when the meeting produces nothing (neither recording nor recognition) — reject it.</summary>
    public bool ProducesNothing => !RecordingEnabled && !SpeechRecognitionEnabled;
}
