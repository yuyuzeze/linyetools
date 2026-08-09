namespace KikuCaption.Recording.Muxing;

/// <summary>Observable audio-timeline metrics (samples, silence, drops, clock error).</summary>
public sealed record AudioTimelineMetrics(
    long WrittenSamples,
    long ExpectedSamples,
    long InsertedSilenceSamples,
    long DroppedLateSamples,
    long RealSamplesWritten,
    long MaxBufferDepthBytes,
    double ClockErrorMs);

/// <summary>
/// Continuous 16 kHz/mono/int16 audio timeline driven by a monotonic clock (PROJECT.md 5.3, M5
/// 修正). Every produced instant maps to <c>floor(elapsed × 16000)</c> samples; real WASAPI PCM is
/// placed from a bounded jitter buffer (arrival-order FIFO), and gaps (startup, silence, missing
/// callbacks) are filled with digital silence — so the audio track never shortens or drifts and
/// silent regions are preserved. Pure and testable with an injected clock (no real waiting).
/// </summary>
public sealed class AudioTimeline
{
    public const int SampleRate = 16000;
    private const int BytesPerSample = 2;

    private readonly int _frameSamples;
    private readonly int _frameBytes;
    private readonly int _maxJitterBytes;
    private readonly object _gate = new();
    private readonly Queue<byte> _jitter = new();

    private long _writtenSamples;
    private long _insertedSilenceSamples;
    private long _droppedLateSamples;
    private long _realSamplesWritten;
    private long _maxBufferDepthBytes;

    public AudioTimeline(int frameMilliseconds = 20, double maxJitterSeconds = 1.0)
    {
        _frameSamples = SampleRate * frameMilliseconds / 1000;
        _frameBytes = _frameSamples * BytesPerSample;
        _maxJitterBytes = (int)(SampleRate * BytesPerSample * maxJitterSeconds);
    }

    public int FrameSamples => _frameSamples;

    public long WrittenSamples { get { lock (_gate) { return _writtenSamples; } } }

    public static long ExpectedSamples(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * SampleRate);

    public AudioTimelineMetrics GetMetrics(TimeSpan now)
    {
        lock (_gate)
        {
            long expected = ExpectedSamples(now);
            return new AudioTimelineMetrics(
                _writtenSamples, expected, _insertedSilenceSamples, _droppedLateSamples,
                _realSamplesWritten, _maxBufferDepthBytes, (expected - _writtenSamples) / 16.0);
        }
    }

    /// <summary>Enqueues real PCM (called from the WASAPI thread; never blocks). Overflow drops the
    /// oldest samples and counts them, keeping memory bounded.</summary>
    public void AppendRealPcm(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var b in pcm)
            {
                _jitter.Enqueue(b);
            }

            while (_jitter.Count > _maxJitterBytes)
            {
                _jitter.Dequeue();
                _jitter.Dequeue();
                _droppedLateSamples++;
            }

            if (_jitter.Count > _maxBufferDepthBytes)
            {
                _maxBufferDepthBytes = _jitter.Count;
            }
        }
    }

    /// <summary>Produces whole 20 ms frames of continuous PCM due up to <paramref name="now"/>.</summary>
    public byte[] ProduceUpTo(TimeSpan now)
    {
        lock (_gate)
        {
            long expected = ExpectedSamples(now);
            long due = expected - _writtenSamples;
            if (due < _frameSamples)
            {
                return Array.Empty<byte>();
            }

            long frames = due / _frameSamples;
            var output = new byte[frames * _frameBytes];
            int offset = 0;

            for (long f = 0; f < frames; f++)
            {
                if (_jitter.Count >= _frameBytes)
                {
                    for (int i = 0; i < _frameBytes; i++)
                    {
                        output[offset++] = _jitter.Dequeue();
                    }

                    _realSamplesWritten += _frameSamples;
                }
                else
                {
                    offset += _frameBytes; // leave zeros = digital silence
                    _insertedSilenceSamples += _frameSamples;
                }
            }

            _writtenSamples += frames * _frameSamples;
            return output;
        }
    }

    /// <summary>Pads/trims to exactly the target sample count for the session end. Never over-writes.</summary>
    public byte[] Flush(TimeSpan end)
    {
        lock (_gate)
        {
            long target = ExpectedSamples(end);
            if (target <= _writtenSamples)
            {
                return Array.Empty<byte>();
            }

            long due = target - _writtenSamples;
            var output = new byte[due * BytesPerSample];
            int offset = 0;

            for (long s = 0; s < due; s++)
            {
                if (_jitter.Count >= BytesPerSample)
                {
                    output[offset++] = _jitter.Dequeue();
                    output[offset++] = _jitter.Dequeue();
                    _realSamplesWritten++;
                }
                else
                {
                    offset += BytesPerSample;
                    _insertedSilenceSamples++;
                }
            }

            _writtenSamples += due;
            return output;
        }
    }

    /// <summary>Resets the timeline at the recording epoch (drops pre-epoch warm-up audio).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _jitter.Clear();
            _writtenSamples = 0;
            _insertedSilenceSamples = 0;
            _droppedLateSamples = 0;
            _realSamplesWritten = 0;
            _maxBufferDepthBytes = 0;
        }
    }
}
