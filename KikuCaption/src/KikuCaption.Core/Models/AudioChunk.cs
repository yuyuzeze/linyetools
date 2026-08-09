namespace KikuCaption.Core.Models;

/// <summary>
/// A block of captured PCM audio in the recognition format
/// (16 kHz, mono, signed 16-bit little-endian by default; PROJECT.md 5.2, 8.1).
/// </summary>
public sealed record AudioChunk(
    ReadOnlyMemory<byte> Pcm,
    TimeSpan Timestamp,
    TimeSpan Duration,
    int SampleRate = 16000,
    int Channels = 1);
