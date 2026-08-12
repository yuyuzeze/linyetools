namespace KikuCaption.Audio.Mixing;

/// <summary>Observable mix metrics (numbers only — never PCM). Balances the audio accounting.</summary>
public sealed record AudioMixMetrics(
    long MixedSamples,
    long ExpectedSamples,
    long SystemRealSamples,
    long MicRealSamples,
    long SystemSilenceSamples,
    long MicSilenceSamples,
    long SystemDroppedSamples,
    long MicDroppedSamples,
    long ClippedSamples,
    long MaxSystemDepthBytes,
    long MaxMicDepthBytes,
    double ClockErrorMs);

/// <summary>
/// Pure, deterministic two-input audio mixer on a single monotonic session clock (UI-R5A). It mirrors
/// the proven <c>AudioTimeline</c> design: each produced instant maps to <c>floor(elapsed × 16000)</c>
/// mixed samples; each input (system loopback, microphone) has its own bounded jitter buffer, and a
/// frame is filled from whatever real PCM is available — the other input contributing digital silence
/// when it has none. The two inputs are summed with <b>saturating</b> int16 addition (no wrap-around).
///
/// Consequences that satisfy the R5A invariants: one input being silent never shortens the mixed
/// timeline; a late-starting microphone loses no system audio (system plays, mic is silent until its
/// PCM arrives); a paused loopback does not drop the microphone; buffers are bounded (oldest samples
/// dropped and counted); and the clock is injected, so 30 minutes of logical time can be simulated
/// with no accumulated drift and no real waiting. All output is 16 kHz / mono / int16 little-endian.
/// </summary>
public sealed class AudioMixTimeline
{
    public const int SampleRate = 16000;
    private const int BytesPerSample = 2;

    private readonly int _frameSamples;
    private readonly int _frameBytes;
    private readonly int _maxJitterBytes;
    private readonly object _gate = new();
    private readonly Queue<byte> _system = new();
    private readonly Queue<byte> _mic = new();

    private long _mixedSamples;
    private long _systemRealSamples;
    private long _micRealSamples;
    private long _systemSilenceSamples;
    private long _micSilenceSamples;
    private long _systemDroppedSamples;
    private long _micDroppedSamples;
    private long _clippedSamples;
    private long _maxSystemDepthBytes;
    private long _maxMicDepthBytes;

    public AudioMixTimeline(int frameMilliseconds = 20, double maxJitterSeconds = 1.0)
    {
        _frameSamples = SampleRate * frameMilliseconds / 1000;
        _frameBytes = _frameSamples * BytesPerSample;
        _maxJitterBytes = (int)(SampleRate * BytesPerSample * maxJitterSeconds);
    }

    public int FrameSamples => _frameSamples;

    public static long ExpectedSamples(TimeSpan elapsed) => (long)(elapsed.TotalSeconds * SampleRate);

    public AudioMixMetrics GetMetrics(TimeSpan now)
    {
        lock (_gate)
        {
            long expected = ExpectedSamples(now);
            return new AudioMixMetrics(
                _mixedSamples, expected, _systemRealSamples, _micRealSamples,
                _systemSilenceSamples, _micSilenceSamples, _systemDroppedSamples, _micDroppedSamples,
                _clippedSamples, _maxSystemDepthBytes, _maxMicDepthBytes,
                (expected - _mixedSamples) / 16.0);
        }
    }

    /// <summary>Enqueues real system-loopback PCM (non-blocking). Overflow drops oldest + counts it.</summary>
    public void AppendSystem(ReadOnlySpan<byte> pcm) => Append(pcm, _system, ref _systemDroppedSamples, ref _maxSystemDepthBytes);

    /// <summary>Enqueues real microphone PCM (non-blocking). Overflow drops oldest + counts it.</summary>
    public void AppendMic(ReadOnlySpan<byte> pcm) => Append(pcm, _mic, ref _micDroppedSamples, ref _maxMicDepthBytes);

    private void Append(ReadOnlySpan<byte> pcm, Queue<byte> queue, ref long dropped, ref long maxDepth)
    {
        if (pcm.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var b in pcm)
            {
                queue.Enqueue(b);
            }

            while (queue.Count > _maxJitterBytes)
            {
                queue.Dequeue();
                queue.Dequeue();
                dropped++;
            }

            if (queue.Count > maxDepth)
            {
                maxDepth = queue.Count;
            }
        }
    }

    /// <summary>Produces whole 20 ms frames of mixed PCM due up to <paramref name="now"/>.</summary>
    public byte[] ProduceUpTo(TimeSpan now)
    {
        lock (_gate)
        {
            long expected = ExpectedSamples(now);
            long due = expected - _mixedSamples;
            if (due < _frameSamples)
            {
                return Array.Empty<byte>();
            }

            long frames = due / _frameSamples;
            var output = new byte[frames * _frameBytes];
            int offset = 0;

            for (long f = 0; f < frames; f++)
            {
                bool systemHas = _system.Count >= _frameBytes;
                bool micHas = _mic.Count >= _frameBytes;

                for (int s = 0; s < _frameSamples; s++)
                {
                    short a = systemHas ? DequeueSample(_system) : (short)0;
                    short b = micHas ? DequeueSample(_mic) : (short)0;
                    short mixed = SaturatingAdd(a, b);
                    output[offset++] = (byte)(mixed & 0xFF);
                    output[offset++] = (byte)((mixed >> 8) & 0xFF);
                }

                if (systemHas) { _systemRealSamples += _frameSamples; } else { _systemSilenceSamples += _frameSamples; }
                if (micHas) { _micRealSamples += _frameSamples; } else { _micSilenceSamples += _frameSamples; }
            }

            _mixedSamples += frames * _frameSamples;
            return output;
        }
    }

    /// <summary>Pads to exactly the target sample count for the session end (mixing any remaining PCM).</summary>
    public byte[] Flush(TimeSpan end)
    {
        lock (_gate)
        {
            long target = ExpectedSamples(end);
            if (target <= _mixedSamples)
            {
                return Array.Empty<byte>();
            }

            long due = target - _mixedSamples;
            var output = new byte[due * BytesPerSample];
            int offset = 0;

            for (long s = 0; s < due; s++)
            {
                short a = _system.Count >= BytesPerSample ? DequeueSample(_system) : (short)0;
                short b = _mic.Count >= BytesPerSample ? DequeueSample(_mic) : (short)0;
                if (a != 0) { _systemRealSamples++; } else { _systemSilenceSamples++; }
                if (b != 0) { _micRealSamples++; } else { _micSilenceSamples++; }
                short mixed = SaturatingAdd(a, b);
                output[offset++] = (byte)(mixed & 0xFF);
                output[offset++] = (byte)((mixed >> 8) & 0xFF);
            }

            _mixedSamples += due;
            return output;
        }
    }

    private static short DequeueSample(Queue<byte> q)
    {
        byte lo = q.Dequeue();
        byte hi = q.Dequeue();
        return (short)(lo | (hi << 8));
    }

    // Sum two int16 samples and clamp to the int16 range — never wraps around (saturating add).
    private short SaturatingAdd(short a, short b)
    {
        int sum = a + b;
        if (sum > short.MaxValue) { _clippedSamples++; return short.MaxValue; }
        if (sum < short.MinValue) { _clippedSamples++; return short.MinValue; }
        return (short)sum;
    }
}
