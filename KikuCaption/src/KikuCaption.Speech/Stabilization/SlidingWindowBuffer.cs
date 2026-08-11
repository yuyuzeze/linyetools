namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// The real sliding audio window (PROJECT.md 9). Holds the current utterance's PCM and its absolute
/// start time; the audio actually sent to Whisper is capped to <c>WindowSeconds</c> (bounding RTF),
/// and on a continuous-speech advance the last <c>OverlapSeconds</c> is retained for continuity.
/// Pure and unit-testable (16 kHz / mono / int16).
/// </summary>
public sealed class SlidingWindowBuffer
{
    public const int BytesPerSecond = 16000 * 2;

    private readonly int _windowBytes;
    private readonly int _overlapBytes;
    private readonly List<byte> _bytes = new();
    private TimeSpan _startTime;

    public SlidingWindowBuffer(double windowSeconds, double overlapSeconds)
    {
        _windowBytes = Even((int)(windowSeconds * BytesPerSecond));
        _overlapBytes = Even((int)(overlapSeconds * BytesPerSecond));
    }

    /// <summary>Absolute start time of the first buffered byte.</summary>
    public TimeSpan StartTime => _startTime;

    public int ByteCount => _bytes.Count;

    public double DurationSeconds => _bytes.Count / (double)BytesPerSecond;

    public void Append(ReadOnlySpan<byte> pcm, TimeSpan chunkStartTime)
    {
        if (_bytes.Count == 0)
        {
            _startTime = chunkStartTime;
        }

        for (int i = 0; i < pcm.Length; i++)
        {
            _bytes.Add(pcm[i]);
        }
    }

    /// <summary>
    /// The audio to transcribe this cycle: the most recent <c>WindowSeconds</c> of the buffer. This
    /// is what genuinely caps the inference input regardless of how long speech has run.
    /// </summary>
    public byte[] TranscriptionWindow(out TimeSpan windowStart)
    {
        if (_bytes.Count <= _windowBytes)
        {
            windowStart = _startTime;
            return _bytes.ToArray();
        }

        int skip = _bytes.Count - _windowBytes;
        windowStart = _startTime + TimeSpan.FromSeconds(skip / (double)BytesPerSecond);
        return _bytes.GetRange(skip, _windowBytes).ToArray();
    }

    /// <summary>True once buffered audio exceeds the window (time to finalize + advance).</summary>
    public bool ExceedsWindow => _bytes.Count > _windowBytes;

    /// <summary>
    /// Drops the front, keeping only the last <c>OverlapSeconds</c> as context for the next window,
    /// and re-anchors the start time to <paramref name="windowEndTime"/> − overlap (monotonic).
    /// </summary>
    public void AdvanceKeepingOverlap(TimeSpan windowEndTime)
    {
        if (_bytes.Count > _overlapBytes)
        {
            int drop = _bytes.Count - _overlapBytes;
            _bytes.RemoveRange(0, drop);
        }

        double keptSeconds = _bytes.Count / (double)BytesPerSecond;
        var newStart = windowEndTime - TimeSpan.FromSeconds(keptSeconds);
        _startTime = newStart < TimeSpan.Zero ? TimeSpan.Zero : newStart;
    }

    public void Clear()
    {
        _bytes.Clear();
        _startTime = default;
    }

    private static int Even(int n) => n - (n % 2);
}
