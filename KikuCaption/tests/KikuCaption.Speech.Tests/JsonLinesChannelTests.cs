using System.Linq;
using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.Worker;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class JsonLinesChannelTests
{
    [Fact]
    public async Task SendAsync_SerializesConcurrentWrites_NoInterleaving()
    {
        var writer = new StringWriter();
        await using var channel = new JsonLinesChannel(new StringReader(string.Empty), writer);

        var tasks = Enumerable.Range(0, 50).Select(i =>
            channel.SendAsync(new ProtocolMessage { Type = "audio", SessionId = "s", Seq = i }, CancellationToken.None));
        await Task.WhenAll(tasks);

        var lines = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        Assert.Equal(50, lines.Count);
        var seqs = lines.Select(l => JsonLinesCodec.Parse(l).Seq).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task ReadMessages_ParsesInOrder_AndSkipsMalformedLines()
    {
        var input = string.Join("\n", new[]
        {
            JsonLinesCodec.Serialize(new ProtocolMessage { Type = "ready", SessionId = "s", Seq = 1 }),
            "this-is-not-json",
            JsonLinesCodec.Serialize(new ProtocolMessage { Type = "flushed", SessionId = "s", Seq = 2, Count = 0 })
        }) + "\n";

        await using var channel = new JsonLinesChannel(new StringReader(input), new StringWriter(), capacity: 4);

        var received = new List<ProtocolMessage>();
        await foreach (var message in channel.ReadMessagesAsync(CancellationToken.None))
        {
            received.Add(message);
        }

        Assert.Equal(2, received.Count);
        Assert.Equal("ready", received[0].Type);
        Assert.Equal("flushed", received[1].Type);
    }

    [Fact]
    public async Task ReadMessages_WithCapacityOne_YieldsAllInOrder()
    {
        var input = string.Join("\n", Enumerable.Range(0, 5).Select(i =>
            JsonLinesCodec.Serialize(new ProtocolMessage { Type = "partial", SessionId = "s", Seq = i }))) + "\n";

        await using var channel = new JsonLinesChannel(new StringReader(input), new StringWriter(), capacity: 1);

        var seqs = new List<long>();
        await foreach (var message in channel.ReadMessagesAsync(CancellationToken.None))
        {
            seqs.Add(message.Seq);
        }

        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, seqs);
    }
}
