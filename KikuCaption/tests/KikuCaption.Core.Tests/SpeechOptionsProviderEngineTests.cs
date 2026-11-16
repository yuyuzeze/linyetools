using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

public class SpeechOptionsProviderEngineTests
{
    private sealed class Selection : ISpeechEngineSelection
    {
        public SpeechEngine Engine { get; init; }
        public string? SpeechCppModel { get; init; } = "haas-ja-v1.0";
    }

    private static SpeechOptionsProvider Provider(ISpeechEngineSelection selection) => new(
        new SpeechOptions { Model = "small", Device = "cpu", ComputeType = "int8", BeamSize = 2, Language = "ja" },
        new Dictionary<string, SpeechContext>
        {
            ["ja"] = new("日本語の技術会議", new[] { "Azure" }),
        },
        modelLocator: null,
        engineSelection: selection);

    [Fact]
    public void SpeechCpp_Ja_Realtime_UsesSpeechCppModel_NoPromptNoTimestamps()
    {
        var o = Provider(new Selection { Engine = SpeechEngine.SpeechCpp })
            .ForLanguage("ja", WhisperModelPurpose.Realtime);

        Assert.Equal(SpeechEngine.SpeechCpp, o.Engine);
        Assert.Equal("haas-ja-v1.0", o.Model);
        Assert.Null(o.InitialPrompt);
        Assert.Null(o.Hotwords);
        Assert.Null(o.Keywords);
        Assert.False(o.RequestTimestamps);
    }

    [Fact]
    public void SpeechCpp_Ja_Wav_RequestsTimestamps()
    {
        var o = Provider(new Selection { Engine = SpeechEngine.SpeechCpp })
            .ForLanguage("ja", WhisperModelPurpose.WavRecognition);

        Assert.Equal(SpeechEngine.SpeechCpp, o.Engine);
        Assert.True(o.RequestTimestamps);
    }

    [Fact]
    public void SpeechCpp_Correction_StaysWhisper()
    {
        var o = Provider(new Selection { Engine = SpeechEngine.SpeechCpp })
            .ForLanguage("ja", WhisperModelPurpose.PostMeetingCorrection);

        Assert.Equal(SpeechEngine.Whisper, o.Engine);
        Assert.Equal("日本語の技術会議", o.InitialPrompt); // Whisper context still applied
    }

    [Fact]
    public void SpeechCpp_Zh_StaysWhisper_BecauseJapaneseOnly()
    {
        var o = Provider(new Selection { Engine = SpeechEngine.SpeechCpp })
            .ForLanguage("zh", WhisperModelPurpose.Realtime);

        Assert.Equal(SpeechEngine.Whisper, o.Engine);
    }

    [Fact]
    public void WhisperSelection_StaysWhisper()
    {
        var o = Provider(new Selection { Engine = SpeechEngine.Whisper })
            .ForLanguage("ja", WhisperModelPurpose.Realtime);

        Assert.Equal(SpeechEngine.Whisper, o.Engine);
        Assert.Equal("small", o.Model);
    }
}
