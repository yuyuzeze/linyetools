using KikuCaption.Core.Session;
using Xunit;

namespace KikuCaption.Core.Tests;

/// <summary>
/// UI-R6A: the immutable per-meeting capability snapshot. Translation and post-meeting correction
/// both require speech recognition; recording-only means neither runs. The snapshot is computed once
/// at meeting start so a mid-meeting settings change cannot affect the running meeting.
/// </summary>
public class SessionCapabilitiesTests
{
    [Fact] // full meeting: recording + speech + translation + correction all on
    public void Compute_AllOn()
    {
        var c = SessionCapabilities.Compute(recordingAvailable: true, speechRecognitionEnabled: true,
            translationEffective: true, postMeetingCorrectionRequested: true);

        Assert.True(c.RecordingEnabled);
        Assert.True(c.SpeechRecognitionEnabled);
        Assert.True(c.TranslationEnabled);
        Assert.True(c.PostMeetingCorrectionEnabled);
        Assert.False(c.IsRecordingOnly);
        Assert.False(c.ProducesNothing);
    }

    [Fact] // R6A: speech off forces translation + correction off, even when requested
    public void Compute_SpeechOff_DisablesTranslationAndCorrection()
    {
        var c = SessionCapabilities.Compute(recordingAvailable: true, speechRecognitionEnabled: false,
            translationEffective: true, postMeetingCorrectionRequested: true);

        Assert.True(c.RecordingEnabled);
        Assert.False(c.SpeechRecognitionEnabled);
        Assert.False(c.TranslationEnabled);          // gated by speech
        Assert.False(c.PostMeetingCorrectionEnabled); // gated by speech
        Assert.True(c.IsRecordingOnly);
        Assert.False(c.ProducesNothing);
    }

    [Fact] // R6A: neither recording nor speech → nothing to do
    public void Compute_NoRecordingNoSpeech_ProducesNothing()
    {
        var c = SessionCapabilities.Compute(recordingAvailable: false, speechRecognitionEnabled: false,
            translationEffective: true, postMeetingCorrectionRequested: true);

        Assert.True(c.ProducesNothing);
        Assert.False(c.IsRecordingOnly);
    }

    [Fact] // speech on but recording unavailable → captions-only, not recording-only
    public void Compute_SpeechOnly_NotRecordingOnly()
    {
        var c = SessionCapabilities.Compute(recordingAvailable: false, speechRecognitionEnabled: true,
            translationEffective: false, postMeetingCorrectionRequested: false);

        Assert.False(c.IsRecordingOnly);
        Assert.False(c.ProducesNothing);
        Assert.True(c.SpeechRecognitionEnabled);
    }

    [Fact] // translation ineffective (same source/target) stays off even with speech on
    public void Compute_TranslationIneffective_Off()
    {
        var c = SessionCapabilities.Compute(recordingAvailable: true, speechRecognitionEnabled: true,
            translationEffective: false, postMeetingCorrectionRequested: false);

        Assert.False(c.TranslationEnabled);
    }
}
