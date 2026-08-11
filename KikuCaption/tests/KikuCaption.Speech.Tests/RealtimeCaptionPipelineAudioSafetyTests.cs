using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// Data-loss Hotfix regression suite (see the Hotfix completion report). Proves the "complete
/// current utterance" buffering never silently discards audio that has not yet produced a final,
/// even under fast/continuous speech, a never-stabilizing Stable Prefix, concurrent ingestion
/// during inference, multi-final splitting, and explicit Stop — and that a silence-only utterance
/// never fabricates a hallucinated caption while a genuinely quiet-but-present utterance is never
/// suppressed.
/// </summary>
public class RealtimeCaptionPipelineAudioSafetyTests
{
    private const int BytesPerSecond = 16000 * 2;

    // ---------- fakes ----------

    /// <summary>
    /// Reconstructs which ORIGINAL token blocks are present in whatever it receives, purely from
    /// total PCM duration. Robust to the pipeline restarting a smaller "leftover" snapshot after a
    /// finalize: a drop in total duration versus the previous call is treated as "a finalize just
    /// happened", advancing a cumulative global-position offset so the correct, position-aware
    /// slice of `tokens` is always returned for whatever audio is currently buffered.
    /// </summary>
    private sealed class TimedTokenRecognizer : ISpeechRecognizer
    {
        private readonly string[] _tokens;
        private readonly double _blockSeconds;
        private readonly int _delayMs;
        private readonly Action<double>? _onSnapshotReceived;
        private double _cumulativeOffsetSeconds;
        private double _lastSeenTotalSeconds;
        private int _calls;

        public TimedTokenRecognizer(string[] tokens, double blockSeconds, int delayMs = 0, Action<double>? onSnapshotReceived = null)
        {
            _tokens = tokens;
            _blockSeconds = blockSeconds;
            _delayMs = delayMs;
            _onSnapshotReceived = onSnapshotReceived;
        }

        public int Calls => _calls;
        public SpeechOptions? LastOptions { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long bytes = 0;
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                bytes += chunk.Pcm.Length;
            }

            Interlocked.Increment(ref _calls);
            double totalSeconds = bytes / (double)BytesPerSecond;

            if (totalSeconds + 1e-6 < _lastSeenTotalSeconds)
            {
                // The buffer shrank vs. the previous call: a finalize+clear happened in between.
                _cumulativeOffsetSeconds += _lastSeenTotalSeconds;
            }

            _lastSeenTotalSeconds = totalSeconds;
            _onSnapshotReceived?.Invoke(totalSeconds);

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            int fromBlock = Math.Clamp((int)Math.Floor(_cumulativeOffsetSeconds / _blockSeconds + 1e-6), 0, _tokens.Length);
            int toBlock = Math.Clamp((int)Math.Floor((_cumulativeOffsetSeconds + totalSeconds) / _blockSeconds + 1e-6), 0, _tokens.Length);
            string text = toBlock > fromBlock ? string.Concat(_tokens[fromBlock..toBlock]) : string.Empty;

            if (text.Length > 0)
            {
                yield return new TranscriptUpdate
                {
                    SessionId = Guid.Empty,
                    Kind = TranscriptUpdateKind.FinalCandidate,
                    StartTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromSeconds(totalSeconds),
                    Text = text,
                    Sequence = _calls
                };
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Echoes exactly how many seconds of audio it received, for exact byte-accounting proofs.</summary>
    private sealed class DurationEchoRecognizer : ISpeechRecognizer
    {
        private readonly int _delayMs;
        private readonly Action<double>? _onSnapshotReceived;

        public DurationEchoRecognizer(int delayMs = 0, Action<double>? onSnapshotReceived = null)
        {
            _delayMs = delayMs;
            _onSnapshotReceived = onSnapshotReceived;
        }

        public SpeechOptions? LastOptions { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            long bytes = 0;
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                bytes += chunk.Pcm.Length;
            }

            double totalSeconds = bytes / (double)BytesPerSecond;
            _onSnapshotReceived?.Invoke(totalSeconds);

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            if (totalSeconds > 0)
            {
                yield return new TranscriptUpdate
                {
                    SessionId = Guid.Empty,
                    Kind = TranscriptUpdateKind.FinalCandidate,
                    StartTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromSeconds(totalSeconds),
                    Text = $"BLOCK_{totalSeconds:0.0}s",
                    Sequence = 1
                };
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Never stabilizes: each call's candidate starts with a different leading rune.</summary>
    private sealed class UnstablePrefixRecognizer : ISpeechRecognizer
    {
        private int _calls;
        public SpeechOptions? LastOptions { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken)) { }
            int n = Interlocked.Increment(ref _calls);
            char lead = (char)('A' + (n % 5)); // rotates through 5 leading chars; window <=3 never repeats
            yield return new TranscriptUpdate
            {
                SessionId = Guid.Empty,
                Kind = TranscriptUpdateKind.FinalCandidate,
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(1),
                Text = $"{lead}変化する内容{n}",
                Sequence = n
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Always returns the same fixed text regardless of what audio it receives.</summary>
    private sealed class FixedTextRecognizer : ISpeechRecognizer
    {
        private readonly string _text;
        private int _seq;
        public FixedTextRecognizer(string text) => _text = text;
        public SpeechOptions? LastOptions { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken)) { }
            yield return new TranscriptUpdate
            {
                SessionId = Guid.Empty,
                Kind = TranscriptUpdateKind.FinalCandidate,
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(1),
                Text = _text,
                Sequence = Interlocked.Increment(ref _seq)
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---------- audio generators ----------

    private static byte[] LoudBlock(double seconds)
    {
        int bytes = (int)Math.Round(seconds * BytesPerSecond);
        bytes -= bytes % 2;
        var pcm = new byte[bytes];
        for (int i = 0; i < bytes / 2; i++)
        {
            BitConverter.GetBytes((short)8000).CopyTo(pcm, i * 2);
        }

        return pcm;
    }

    private static byte[] QuietBlock(double seconds)
    {
        int bytes = (int)Math.Round(seconds * BytesPerSecond);
        bytes -= bytes % 2;
        return new byte[bytes]; // all-zero = well below any RMS silence threshold
    }

    /// <summary>Pushes <paramref name="blockCount"/> loud blocks of <paramref name="blockSeconds"/>
    /// each, with a small real-world delay between pushes so the pipeline's cycle loop can interleave.
    /// Timestamps are exact, cumulative LOGICAL audio position (not wall-clock), so finals stay
    /// contiguous regardless of scheduling jitter.</summary>
    private static async IAsyncEnumerable<AudioChunk> LoudBlocks(
        int blockCount, double blockSeconds, int delayMsBetweenBlocks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var t = TimeSpan.Zero;
        for (int i = 0; i < blockCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioChunk(LoudBlock(blockSeconds), t, TimeSpan.FromSeconds(blockSeconds));
            t += TimeSpan.FromSeconds(blockSeconds);
            if (delayMsBetweenBlocks > 0)
            {
                await Task.Delay(delayMsBetweenBlocks, cancellationToken);
            }
        }
    }

    /// <summary>Like <see cref="LoudBlocks"/> but each unit is <paramref name="speechSeconds"/> of loud
    /// audio followed by <paramref name="pauseSeconds"/> of silence — simulating short pauses between
    /// phrases in fast, continuous speech.</summary>
    private static async IAsyncEnumerable<AudioChunk> LoudBlocksWithPauses(
        int blockCount, double speechSeconds, double pauseSeconds, int delayMsBetweenBlocks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var t = TimeSpan.Zero;
        for (int i = 0; i < blockCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioChunk(LoudBlock(speechSeconds), t, TimeSpan.FromSeconds(speechSeconds));
            t += TimeSpan.FromSeconds(speechSeconds);
            yield return new AudioChunk(QuietBlock(pauseSeconds), t, TimeSpan.FromSeconds(pauseSeconds));
            t += TimeSpan.FromSeconds(pauseSeconds);
            if (delayMsBetweenBlocks > 0)
            {
                await Task.Delay(delayMsBetweenBlocks, cancellationToken);
            }
        }
    }

    private static async IAsyncEnumerable<AudioChunk> SilentChunks(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken);
            yield return new AudioChunk(new byte[320], TimeSpan.FromMilliseconds(i * 10), TimeSpan.FromMilliseconds(10));
        }
    }

    private static async IAsyncEnumerable<AudioChunk> QuietThenLoudThenQuiet(
        double quietBefore, double loud, double quietAfter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var t = TimeSpan.Zero;
        yield return new AudioChunk(QuietBlock(quietBefore), t, TimeSpan.FromSeconds(quietBefore));
        t += TimeSpan.FromSeconds(quietBefore);
        await Task.Delay(5, cancellationToken);
        yield return new AudioChunk(LoudBlock(loud), t, TimeSpan.FromSeconds(loud));
        t += TimeSpan.FromSeconds(loud);
        await Task.Delay(5, cancellationToken);
        yield return new AudioChunk(QuietBlock(quietAfter), t, TimeSpan.FromSeconds(quietAfter));
    }

    private static async IAsyncEnumerable<AudioChunk> InfiniteLoudChunks([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var t = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
            yield return new AudioChunk(LoudBlock(0.01), t, TimeSpan.FromMilliseconds(10));
            t += TimeSpan.FromMilliseconds(10);
        }
    }

    /// <summary>A caller-controlled, push-based audio source (for the concurrent-append test).</summary>
    private static (IAsyncEnumerable<AudioChunk> Source, Action<AudioChunk> Push, Action Complete) ControlledSource()
    {
        var channel = Channel.CreateUnbounded<AudioChunk>();
        async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }

        return (Read(), c => channel.Writer.TryWrite(c), () => channel.Writer.TryComplete());
    }

    private static RealtimeCaptionPipeline Create(ISpeechRecognizer recognizer, ProgressiveCaptionOptions options)
        => new(() => recognizer, options, new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }), NullLogger<RealtimeCaptionPipeline>.Instance);

    // ---------- test 1: 12 seconds continuous, no silence, no punctuation ----------

    [Fact]
    public async Task Continuous12Seconds_AllTokensPresent_ExactlyOnce_NoLoss()
    {
        var tokens = new[] { "A", "B", "C", "D", "E", "F" };
        var recognizer = new TimedTokenRecognizer(tokens, blockSeconds: 2.0);
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 12, MaxWaitSeconds = 20
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(LoudBlocks(6, 2.0, delayMsBetweenBlocks: 0), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        string all = string.Concat(finals);
        foreach (var tok in tokens)
        {
            Assert.Equal(1, CountOccurrences(all, tok)); // each token exactly once — not just DEF
        }

        Assert.Equal("ABCDEF", all); // full coverage, correct order, no gaps, no duplicates
        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
    }

    // ---------- test 2: ~30 seconds fast continuous speech with short (300ms) pauses ----------

    [Fact]
    public async Task FastContinuousSpeech_30Seconds_WithShortPauses_NoLoss_NoDuplicate_Monotonic()
    {
        const int blockCount = 13;
        const double speechSeconds = 2.0;
        const double pauseSeconds = 0.3; // below SilenceFinalMs (700ms) — must not fragment
        var tokens = Enumerable.Range(0, blockCount).Select(i => $"T{i}_").ToArray();
        var recognizer = new TimedTokenRecognizer(tokens, blockSeconds: speechSeconds + pauseSeconds);
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 12, MaxWaitSeconds = 20
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<CaptionFinalEventArgs>();
        pipeline.FinalProduced += (_, e) => finals.Add(e);

        await pipeline.StartAsync(LoudBlocksWithPauses(blockCount, speechSeconds, pauseSeconds, delayMsBetweenBlocks: 20), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        string all = string.Concat(finals.Select(f => f.Text));
        foreach (var tok in tokens)
        {
            Assert.Equal(1, CountOccurrences(all, tok)); // no loss, no duplicate
        }

        // Order preserved (each token's position in the concatenation is increasing).
        int lastIndex = -1;
        foreach (var tok in tokens)
        {
            int idx = all.IndexOf(tok, StringComparison.Ordinal);
            Assert.True(idx > lastIndex, $"token {tok} out of order");
            lastIndex = idx;
        }

        // final timestamps monotonic
        var prevEnd = TimeSpan.MinValue;
        foreach (var f in finals)
        {
            Assert.True(f.StartTime <= f.EndTime);
            Assert.True(f.StartTime >= prevEnd);
            prevEnd = f.EndTime;
        }

        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
        Assert.Equal(0, pipeline.CurrentMetrics.PendingAudioSeconds, 1); // fully flushed on stop
    }

    // ---------- test 3: Stable Prefix never advances ----------

    [Fact]
    public async Task NeverStabilizingPrefix_AudioNeverDiscardedUncommitted_SomethingIsEventuallyFinalized()
    {
        var recognizer = new UnstablePrefixRecognizer();
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 3, MaxWaitSeconds = 5, RecentCandidates = 2
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(LoudBlocks(3, 2.0, delayMsBetweenBlocks: 30), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEmpty(finals);
        Assert.Contains(finals, t => t.Trim().Length > 0); // output via latest candidate / flush, not lost
        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds); // the critical invariant
    }

    // ---------- test 4: audio arriving DURING inference is preserved ----------

    [Fact]
    public async Task AudioArrivingDuringInference_IsNotDiscarded_ProcessedNextCycle()
    {
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognizer = new DurationEchoRecognizer(delayMs: 400, onSnapshotReceived: secs =>
        {
            if (secs >= 7.99)
            {
                snapshotCaptured.TrySetResult();
            }
        });
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 50, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 7, MaxWaitSeconds = 10
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        var (source, push, complete) = ControlledSource();
        await pipeline.StartAsync(source, "ja", CancellationToken.None);

        // Push 8 seconds (4 x 2s blocks) up front.
        for (int i = 0; i < 4; i++)
        {
            push(new AudioChunk(LoudBlock(2.0), TimeSpan.FromSeconds(i * 2.0), TimeSpan.FromSeconds(2.0)));
        }

        await snapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10)); // a cycle has captured the 8s snapshot and is now "in inference"

        // While that inference (400ms) is still running, 2 MORE seconds arrive.
        push(new AudioChunk(LoudBlock(2.0), TimeSpan.FromSeconds(8.0), TimeSpan.FromSeconds(2.0)));

        await Task.Delay(700); // let the first cycle's finalize complete (400ms delay + processing)
        complete(); // end the source -> forces a flush of the leftover 2s

        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEmpty(finals);
        Assert.Equal("BLOCK_8.0s", finals[0]); // first final = EXACTLY the pre-inference snapshot, not 10s
        Assert.Contains(finals, t => t == "BLOCK_2.0s"); // the concurrently-arrived 2s was preserved, not lost
        Assert.Equal(10.0, pipeline.CurrentMetrics.AudioFinalizedSeconds, 1); // 8 + 2, nothing missing
        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
    }

    // ---------- test 5: MaxSentenceSeconds splits a long utterance with no gaps/dup ----------

    [Fact]
    public async Task MaxSentenceLength_SplitsLongUtterance_ContiguousNoGapsNoDuplicates()
    {
        var tokens = Enumerable.Range(0, 15).Select(i => $"[{i}]").ToArray(); // 15 x 2s = 30s
        var recognizer = new TimedTokenRecognizer(tokens, blockSeconds: 2.0);
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 50, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 12, MaxWaitSeconds = 20
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<CaptionFinalEventArgs>();
        pipeline.FinalProduced += (_, e) => finals.Add(e);

        await pipeline.StartAsync(LoudBlocks(15, 2.0, delayMsBetweenBlocks: 80), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(finals.Count >= 2, $"expected the 30s utterance to split into multiple finals, got {finals.Count}");

        string all = string.Concat(finals.Select(f => f.Text));
        foreach (var tok in tokens)
        {
            Assert.True(1 == CountOccurrences(all, tok), $"token {tok} occurs {CountOccurrences(all, tok)} times");
        }

        int lastIndex = -1;
        foreach (var tok in tokens)
        {
            int idx = all.IndexOf(tok, StringComparison.Ordinal);
            Assert.True(idx > lastIndex, $"token {tok} out of order or missing");
            lastIndex = idx;
        }

        var prevEnd = TimeSpan.MinValue;
        foreach (var f in finals)
        {
            Assert.True(f.StartTime <= f.EndTime);
            Assert.True(f.StartTime >= prevEnd, "gap/overlap between consecutive finals");
            prevEnd = f.EndTime;
        }

        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
    }

    // ---------- test 6: explicit Stop flushes pending (no punctuation, no long pause, unstable prefix) ----------

    [Fact]
    public async Task ExplicitStop_MidUtterance_FlushesPending_NoPunctuation_NoLongPause()
    {
        var recognizer = new FixedTextRecognizer("まだ途中の文"); // no sentence-ending punctuation
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 30, MaxWaitSeconds = 60 // won't trigger on their own
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(InfiniteLoudChunks(), "ja", CancellationToken.None);
        await Task.Delay(400); // let a couple of cycles accumulate pending, uncommitted text

        await pipeline.StopAsync(); // EXPLICIT stop — not natural source exhaustion

        Assert.NotEmpty(finals);
        Assert.Contains(finals, t => t.Contains("まだ途中の文"));
        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
    }

    // ---------- test 7: silence never hallucinates; genuine quiet speech is never suppressed ----------

    [Theory]
    [InlineData("ご視聴ありがとうございました")]
    [InlineData("ありがとうございました")]
    [InlineData("字幕をご覧いただき")]
    public async Task PureSilence_NeverProducesHallucinatedFinal(string hallucinatedPhrase)
    {
        var recognizer = new FixedTextRecognizer(hallucinatedPhrase);
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 8, MaxWaitSeconds = 10
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(SilentChunks(150), "ja", CancellationToken.None); // 1.5s pure silence
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.DoesNotContain(finals, t => t.Contains(hallucinatedPhrase));
        Assert.Empty(finals); // a fully silent utterance must never produce a caption
    }

    [Fact] // regression: a genuinely quiet-but-present utterance (one brief loud moment) is NOT suppressed
    public async Task QuietSpeechWithBriefLoudMoment_IsNotSuppressed()
    {
        var recognizer = new FixedTextRecognizer("本当の発言です。");
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 6, MaxWaitSeconds = 10
        };
        await using var pipeline = Create(recognizer, options);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(QuietThenLoudThenQuiet(quietBefore: 1.0, loud: 0.3, quietAfter: 1.0), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEmpty(finals); // must NOT be suppressed — real (if brief/quiet) speech was present
        Assert.Contains(finals, t => t.Contains("本当の発言です"));
    }

    // ---------- diagnostics ----------

    [Fact]
    public async Task EmptyCandidate_IncrementsDiagnosticCounter()
    {
        var recognizer = new FixedTextRecognizer(""); // FixedTextRecognizer with empty text never yields an update
        var options = new ProgressiveCaptionOptions
        {
            PartialIntervalMs = 60, WindowSeconds = 2, OverlapSeconds = 1,
            SilenceFinalMs = 700, MaxSentenceSeconds = 4, MaxWaitSeconds = 6
        };
        await using var pipeline = Create(recognizer, options);
        await pipeline.StartAsync(LoudBlocks(3, 1.0, delayMsBetweenBlocks: 30), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(pipeline.CurrentMetrics.EmptyCandidateCount > 0);
        Assert.Equal(0, pipeline.CurrentMetrics.AudioDiscardedUncommittedSeconds);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
