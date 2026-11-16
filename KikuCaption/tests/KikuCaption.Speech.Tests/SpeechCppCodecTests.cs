using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.SpeechCpp;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class SpeechCppCodecTests
{
    [Fact]
    public void Serialize_Chunk_OmitsNulls_KeepsSnakeCase()
    {
        var line = SpeechCppCodec.Serialize(new SpeechCppMessage
        {
            Type = "chunk", Id = "r1", Seq = 0, Pcm = "AAAA"
        });

        Assert.Contains("\"type\":\"chunk\"", line);
        Assert.Contains("\"id\":\"r1\"", line);
        Assert.Contains("\"seq\":0", line);
        Assert.Contains("\"pcm\":\"AAAA\"", line);
        Assert.DoesNotContain("sample_rate", line);
        Assert.DoesNotContain("keywords", line);
    }

    [Fact]
    public void Serialize_Transcribe_WithTimestamps_UsesSnakeCaseAndBool()
    {
        var line = SpeechCppCodec.Serialize(new SpeechCppMessage
        {
            Type = "transcribe", Id = "r1", SampleRate = 16000, Timestamps = true
        });

        Assert.Contains("\"sample_rate\":16000", line);
        Assert.Contains("\"timestamps\":true", line);
    }

    [Fact]
    public void Serialize_KeepsJapaneseUnescaped()
    {
        var line = SpeechCppCodec.Serialize(new SpeechCppMessage
        {
            Type = "transcribe", Id = "r1", SampleRate = 16000, Keywords = "Haas=ハース"
        });

        Assert.Contains("ハース", line);
    }

    [Fact]
    public void Parse_End_WithSegments_RoundTrips()
    {
        const string line = "{\"type\":\"end\",\"id\":\"r\",\"text\":\"そう\",\"stop\":\"complete\"," +
                            "\"segments\":[{\"start\":0.0,\"end\":2.48,\"text\":\"そう\"}]}";

        var message = SpeechCppCodec.Parse(line);

        Assert.Equal("end", message.Type);
        Assert.Equal("そう", message.Text);
        var seg = Assert.Single(message.Segments!);
        Assert.Equal(2.48, seg.End, 3);
    }

    [Fact]
    public void Parse_Ready_IgnoresUnknownModelObject()
    {
        const string line = "{\"type\":\"ready\",\"protocol\":4,\"version\":\"0.11.0\",\"defaults\":{}," +
                            "\"model\":{\"name\":\"haas\",\"sample_rate\":16000,\"languages\":[\"ja\"]}}";

        var message = SpeechCppCodec.Parse(line);

        Assert.Equal("ready", message.Type);
        Assert.Equal(4, message.Protocol);
        Assert.Equal("0.11.0", message.Version);
    }

    [Fact]
    public void Parse_Error_ReadsErrorPayload()
    {
        const string line = "{\"type\":\"error\",\"id\":\"a\",\"error\":{\"code\":\"out_of_range\",\"option\":\"speed\",\"message\":\"x\"}}";

        var message = SpeechCppCodec.Parse(line);

        Assert.Equal("out_of_range", message.Error!.Code);
        Assert.Equal("speed", message.Error!.Option);
    }

    [Fact]
    public void Parse_NonObject_Throws()
    {
        Assert.Throws<ProtocolException>(() => SpeechCppCodec.Parse("not json"));
    }

    [Fact]
    public void Parse_MissingType_Throws()
    {
        Assert.Throws<ProtocolException>(() => SpeechCppCodec.Parse("{\"id\":\"r\"}"));
    }
}
