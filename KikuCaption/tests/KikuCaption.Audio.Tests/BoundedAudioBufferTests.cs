using KikuCaption.Audio.Buffering;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Audio.Tests;

public class BoundedAudioBufferTests
{
    private static AudioChunk Chunk() =>
        new(new byte[2], TimeSpan.Zero, TimeSpan.FromMilliseconds(10));

    [Fact]
    public void TryWrite_WhenFull_DropsAndCounts()
    {
        var buffer = new BoundedAudioBuffer(capacity: 2);

        Assert.True(buffer.TryWrite(Chunk()));
        Assert.True(buffer.TryWrite(Chunk()));
        Assert.False(buffer.TryWrite(Chunk())); // full -> dropped
        Assert.False(buffer.TryWrite(Chunk()));

        Assert.Equal(2, buffer.DroppedChunkCount);
    }

    [Fact]
    public async Task Reading_FreesCapacityForMoreWrites()
    {
        var buffer = new BoundedAudioBuffer(capacity: 1);
        Assert.True(buffer.TryWrite(Chunk()));
        Assert.False(buffer.TryWrite(Chunk())); // full

        var read = new List<AudioChunk>();
        buffer.Complete();
        await foreach (var c in buffer.ReadAllAsync(CancellationToken.None))
        {
            read.Add(c);
        }

        Assert.Single(read);
        Assert.Equal(1, buffer.DroppedChunkCount);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAudioBuffer(0));
    }

    [Fact]
    public async Task Complete_WithError_PropagatesToConsumer()
    {
        var buffer = new BoundedAudioBuffer(4);
        buffer.Complete(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in buffer.ReadAllAsync(CancellationToken.None))
            {
            }
        });
    }
}
