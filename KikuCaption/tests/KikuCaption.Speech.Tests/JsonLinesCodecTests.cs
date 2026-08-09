using KikuCaption.Speech.Protocol;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class JsonLinesCodecTests
{
    [Fact]
    public void SerializeThenParse_RoundTrips()
    {
        var message = new ProtocolMessage
        {
            Type = ProtocolConstants.Types.Initialize,
            SessionId = "abc",
            Seq = 7,
            Language = "ja",
            BeamSize = 1
        };

        var line = JsonLinesCodec.Serialize(message);
        var parsed = JsonLinesCodec.Parse(line);

        Assert.Equal("initialize", parsed.Type);
        Assert.Equal("abc", parsed.SessionId);
        Assert.Equal(7, parsed.Seq);
        Assert.Equal("ja", parsed.Language);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void Serialize_OmitsNullFields()
    {
        var line = JsonLinesCodec.Serialize(new ProtocolMessage
        {
            Type = "flush",
            SessionId = "s",
            Seq = 1
        });

        Assert.DoesNotContain("model", line);
        Assert.DoesNotContain("pcm", line);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        var ex = Assert.Throws<ProtocolException>(() => JsonLinesCodec.Parse("{not json"));
        Assert.Equal("invalid_json", ex.Code);
    }

    [Fact]
    public void Parse_VersionMismatch_Throws()
    {
        var line = "{\"v\":99,\"type\":\"flush\",\"sessionId\":\"s\",\"seq\":1}";
        var ex = Assert.Throws<ProtocolException>(() => JsonLinesCodec.Parse(line));
        Assert.Equal("version_mismatch", ex.Code);
    }

    [Theory]
    [InlineData("{\"v\":1,\"sessionId\":\"s\",\"seq\":1}")] // missing type
    [InlineData("{\"v\":1,\"type\":\"flush\",\"seq\":1}")]   // missing sessionId
    public void Parse_MissingRequiredField_Throws(string line)
    {
        var ex = Assert.Throws<ProtocolException>(() => JsonLinesCodec.Parse(line));
        Assert.Equal("missing_field", ex.Code);
    }

    [Fact]
    public void CreateAudio_Valid_ProducesBase64AndFrames()
    {
        var pcm = new byte[320]; // 160 int16 samples
        var message = JsonLinesCodec.CreateAudio("s", 3, pcm);

        Assert.Equal("audio", message.Type);
        Assert.Equal(160, message.Frames);
        Assert.False(string.IsNullOrEmpty(message.Pcm));
    }

    [Fact]
    public void CreateAudio_OddLength_Throws()
    {
        var ex = Assert.Throws<ProtocolException>(() => JsonLinesCodec.CreateAudio("s", 1, new byte[3]));
        Assert.Equal("invalid_pcm", ex.Code);
    }

    [Fact]
    public void CreateAudio_TooLarge_Throws()
    {
        var pcm = new byte[ProtocolConstants.MaxAudioBytes + 2];
        var ex = Assert.Throws<ProtocolException>(() => JsonLinesCodec.CreateAudio("s", 1, pcm));
        Assert.Equal("message_too_large", ex.Code);
    }
}
