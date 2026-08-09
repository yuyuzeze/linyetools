using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

public class ModelTests
{
    [Fact]
    public void AudioChunk_DefaultsToRecognitionFormat()
    {
        var chunk = new AudioChunk(new byte[] { 0, 1, 2, 3 }, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

        Assert.Equal(16000, chunk.SampleRate);
        Assert.Equal(1, chunk.Channels);
        Assert.Equal(4, chunk.Pcm.Length);
    }

    [Fact]
    public void TranscriptSegment_SupportsValueEquality()
    {
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var created = DateTimeOffset.UnixEpoch;

        TranscriptSegment Make() => new()
        {
            Id = id,
            SessionId = sessionId,
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(2),
            Language = "ja",
            Text = "こんにちは",
            Status = TranscriptStatus.Final,
            CreatedAt = created
        };

        Assert.Equal(Make(), Make());
    }

    [Fact]
    public void MeetingSession_RequiresCoreFields()
    {
        var session = new MeetingSession
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UnixEpoch,
            RecognitionLanguage = "zh",
            OutputDirectory = @"C:\Meetings\demo"
        };

        Assert.Null(session.EndedAt);
        Assert.Equal("zh", session.RecognitionLanguage);
    }

    [Fact]
    public void TranscriptStatus_HasExpectedMembers()
    {
        Assert.Equal(4, Enum.GetValues<TranscriptStatus>().Length);
        Assert.True(Enum.IsDefined(TranscriptStatus.TranslationFailed));
    }
}
