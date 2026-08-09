using System.Threading.Channels;
using KikuCaption.Core.Models;

namespace KikuCaption.Audio.Buffering;

/// <summary>
/// Bounded hand-off buffer between the (real-time) WASAPI capture thread and the async
/// consumer. Backed by a bounded <see cref="Channel{T}"/> so memory can never grow without
/// bound (PROJECT.md 6).
///
/// The producer uses <see cref="TryWrite"/>, which never blocks the audio thread: if the
/// buffer is full the chunk is counted as dropped (a measurable back-pressure metric)
/// instead of silently exhausting memory or stalling capture.
/// </summary>
public sealed class BoundedAudioBuffer
{
    private readonly Channel<AudioChunk> _channel;
    private long _droppedChunks;

    public BoundedAudioBuffer(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量必须 >= 1。");
        }

        Capacity = capacity;
        _channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public int Capacity { get; }

    public long DroppedChunkCount => Interlocked.Read(ref _droppedChunks);

    /// <summary>
    /// Non-blocking write. Returns false and increments <see cref="DroppedChunkCount"/> when
    /// the buffer is full.
    /// </summary>
    public bool TryWrite(AudioChunk chunk)
    {
        if (_channel.Writer.TryWrite(chunk))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedChunks);
        return false;
    }

    public IAsyncEnumerable<AudioChunk> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Marks the buffer complete so consumers finish enumerating.</summary>
    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
