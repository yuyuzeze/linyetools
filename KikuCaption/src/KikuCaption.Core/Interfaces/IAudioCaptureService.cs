using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Captures the system output ("what you hear") audio and yields it as a stream of
/// <see cref="AudioChunk"/> already normalized to the recognition format
/// (16 kHz, mono, signed 16-bit little-endian PCM; PROJECT.md 5.2, 8.2).
/// </summary>
/// <remarks>
/// Enumerating <see cref="CaptureAsync"/> starts capture; cancelling the token or breaking
/// out of the enumeration stops it. A single instance captures at most once. Implementations
/// surface device failures by throwing <see cref="AudioCaptureException"/> from the stream.
/// </remarks>
public interface IAudioCaptureService : IAsyncDisposable
{
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}
