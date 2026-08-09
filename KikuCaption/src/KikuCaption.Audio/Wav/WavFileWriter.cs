using System.Runtime.InteropServices;
using NAudio.Wave;

namespace KikuCaption.Audio.Wav;

/// <summary>
/// Minimal WAV writer fixed to the recognition format (16 kHz / mono / 16-bit PCM).
/// Used by the Milestone 1 validation entry to persist captured system audio. Wraps
/// NAudio's <see cref="WaveFileWriter"/>, which finalizes the RIFF header on dispose.
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    public static readonly WaveFormat RecognitionFormat = new(16000, 16, 1);

    private readonly WaveFileWriter _writer;

    public WavFileWriter(string filePath)
    {
        FilePath = filePath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new WaveFileWriter(filePath, RecognitionFormat);
    }

    public string FilePath { get; }

    /// <summary>Bytes of PCM audio data written so far (excludes the header).</summary>
    public long BytesWritten => _writer.Length;

    public void Write(ReadOnlyMemory<byte> pcm)
    {
        if (MemoryMarshal.TryGetArray(pcm, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            _writer.Write(segment.Array, segment.Offset, segment.Count);
        }
        else
        {
            var copy = pcm.ToArray();
            _writer.Write(copy, 0, copy.Length);
        }
    }

    public void Dispose() => _writer.Dispose();
}
