using System.Runtime.CompilerServices;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class RealtimeCaptionPipelineTests
{
    private sealed class ScriptedRecognizer : ISpeechRecognizer
    {
        private readonly Func<int, string> _candidate;
        private readonly int _delayMs;
        private readonly bool _faultOnRecognize;
        private readonly bool _faultOnInit;
        private int _calls;

        public ScriptedRecognizer(Func<int, string> candidate, int delayMs = 0,
            bool faultOnRecognize = false, bool faultOnInit = false)
        {
            _candidate = candidate;
            _delayMs = delayMs;
            _faultOnRecognize = faultOnRecognize;
            _faultOnInit = faultOnInit;
        }

        public int InitCount { get; private set; }
        public bool Disposed { get; private set; }
        public SpeechOptions? LastOptions { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            InitCount++;
            LastOptions = options;
            if (_faultOnInit)
            {
                throw new SpeechRecognitionException("init_failed", "boom");
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken)) { }

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            if (_faultOnRecognize)
            {
                throw new SpeechRecognitionException("worker_exited", "worker died");
            }

            int n = ++_calls;
            var text = _candidate(n);
            if (!string.IsNullOrEmpty(text))
            {
                yield return new TranscriptUpdate
                {
                    SessionId = Guid.Empty,
                    Kind = TranscriptUpdateKind.FinalCandidate,
                    StartTime = TimeSpan.Zero,
                    EndTime = TimeSpan.FromSeconds(1),
                    Text = text,
                    Sequence = n
                };
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static ProgressiveCaptionOptions FastOptions() => new()
    {
        PartialIntervalMs = 60,
        SilenceFinalMs = 500,
        MaxSentenceSeconds = 30,
        MaxWaitSeconds = 60
    };

    private static async IAsyncEnumerable<AudioChunk> LoudChunks(int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
            var bytes = new byte[320];
            for (int j = 0; j < 160; j++)
            {
                BitConverter.GetBytes((short)8000).CopyTo(bytes, j * 2);
            }

            yield return new AudioChunk(bytes, TimeSpan.FromMilliseconds(i * 10), TimeSpan.FromMilliseconds(10));
        }
    }

    private static RealtimeCaptionPipeline Create(ISpeechRecognizer recognizer, ProgressiveCaptionOptions? options = null)
        => new(() => recognizer, options ?? FastOptions(),
            new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }), NullLogger<RealtimeCaptionPipeline>.Instance);

    private static RealtimeCaptionPipeline Create(ISpeechRecognizer recognizer, ISpeechOptionsProvider provider, ProgressiveCaptionOptions? options = null)
        => new(() => recognizer, options ?? FastOptions(), provider, NullLogger<RealtimeCaptionPipeline>.Instance);

    [Fact] // tests 1-4: the FULL, per-language config reaches the worker
    public async Task RealtimePipeline_PassesFullSpeechConfigToWorker()
    {
        var recognizer = new ScriptedRecognizer(_ => "はい。");
        var provider = new SpeechOptionsProvider(
            new SpeechOptions { Model = "small", Device = "cpu", ComputeType = "int8", BeamSize = 2, Language = "zh", ModelCacheDirectory = @"C:\models\whisper" },
            new Dictionary<string, SpeechContext>
            {
                ["ja"] = new("技術会議", new[] { "Azure", "OpenAI" }),
                ["zh"] = new("这是技术会议", new[] { "Azure" }),
            });
        await using var pipeline = Create(recognizer, provider);

        await pipeline.StartAsync(LoudChunks(20), "ja", CancellationToken.None); // language chosen at start
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        var got = recognizer.LastOptions!;
        Assert.Equal("small", got.Model);
        Assert.Equal("int8", got.ComputeType);
        Assert.Equal(2, got.BeamSize);                    // test 2: BeamSize actually applied
        Assert.Equal("ja", got.Language);
        Assert.Equal(@"C:\models\whisper", got.ModelCacheDirectory);
        Assert.Equal("技術会議", got.InitialPrompt);        // test 3: ja InitialPrompt (not zh)
        Assert.Equal(new[] { "Azure", "OpenAI" }, got.Hotwords); // test 4: ja Hotwords
    }

    [Fact] // test 5: no context for the language → still works, prompt/hotwords absent
    public async Task RealtimePipeline_NoContext_StillWorks()
    {
        var recognizer = new ScriptedRecognizer(_ => "はい。");
        await using var pipeline = Create(recognizer, new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }));
        await pipeline.StartAsync(LoudChunks(20), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Null(recognizer.LastOptions!.Hotwords);
        Assert.Null(recognizer.LastOptions!.InitialPrompt);
        Assert.Equal(1, recognizer.InitCount);
    }

    [Fact] // test 15: pending text (no sentence punctuation) is flushed as a final on stop
    public async Task RealtimePipeline_FlushesPending_OnStop()
    {
        var recognizer = new ScriptedRecognizer(_ => "まだ途中の文"); // no sentence-ending punctuation
        await using var pipeline = Create(recognizer);
        var finals = new List<string>();
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(LoudChunks(30), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15)); // finite source → flush

        Assert.NotEmpty(finals);
        Assert.Contains(finals, t => t.Contains("まだ途中の文"));
    }

    [Fact] // test 10: emitted final timestamps are monotonic (non-decreasing, start <= end)
    public async Task RealtimePipeline_FinalTimestamps_AreMonotonic()
    {
        int n = 0;
        // Each cycle a longer sentence ending in punctuation → several finals over the run.
        var recognizer = new ScriptedRecognizer(_ => (++n % 2 == 0) ? "今日は会議です。" : "はい。");
        await using var pipeline = Create(recognizer);
        var finals = new List<CaptionFinalEventArgs>();
        pipeline.FinalProduced += (_, e) => finals.Add(e);

        await pipeline.StartAsync(LoudChunks(120), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEmpty(finals);
        var prevEnd = TimeSpan.MinValue;
        foreach (var f in finals)
        {
            Assert.True(f.StartTime <= f.EndTime, "start must not be after end");
            Assert.True(f.StartTime >= prevEnd, "final start must not precede previous final end");
            prevEnd = f.EndTime;
        }
    }

    [Fact]
    public async Task FiniteSource_ProducesPartialAndFinal()
    {
        var recognizer = new ScriptedRecognizer(_ => "你好世界。");
        await using var pipeline = Create(recognizer);
        var partials = new List<string>();
        var finals = new List<string>();
        pipeline.PartialUpdated += (_, e) => partials.Add(e.PartialText);
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(LoudChunks(40), "zh", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotEmpty(partials);
        Assert.NotEmpty(finals);
        Assert.Contains("你好世界", finals[0]);
        Assert.Equal(1, recognizer.InitCount); // model loaded once
        Assert.True(recognizer.Disposed);
        Assert.Equal(CaptionPipelineState.Stopped, pipeline.State);
    }

    [Fact]
    public async Task StateTransitions_RunningThenStopped()
    {
        var states = new List<CaptionPipelineState>();
        var pipeline = Create(new ScriptedRecognizer(_ => "x"));
        pipeline.StateChanged += (_, s) => states.Add(s);

        await pipeline.StartAsync(LoudChunks(20), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains(CaptionPipelineState.Running, states);
        Assert.Contains(CaptionPipelineState.Stopped, states);
    }

    [Fact]
    public async Task RepeatedStart_Throws()
    {
        await using var pipeline = Create(new ScriptedRecognizer(_ => "x"));
        await pipeline.StartAsync(LoudChunks(200), "ja", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.StartAsync(LoudChunks(10), "ja", CancellationToken.None));

        await pipeline.StopAsync();
    }

    [Fact]
    public async Task RepeatedStop_IsSafe()
    {
        var pipeline = Create(new ScriptedRecognizer(_ => "x"));
        await pipeline.StartAsync(LoudChunks(200), "ja", CancellationToken.None);
        await pipeline.StopAsync();
        await pipeline.StopAsync(); // no throw
        Assert.Equal(CaptionPipelineState.Stopped, pipeline.State);
    }

    [Fact]
    public async Task InitFault_Throws_AndDoesNotRun()
    {
        var pipeline = Create(new ScriptedRecognizer(_ => "x", faultOnInit: true));
        await Assert.ThrowsAsync<SpeechRecognitionException>(
            () => pipeline.StartAsync(LoudChunks(10), "ja", CancellationToken.None));
        Assert.Equal(CaptionPipelineState.Faulted, pipeline.State);
    }

    [Fact]
    public async Task RecognizeFault_RaisesFaulted_AndStopsSafely()
    {
        var faulted = false;
        var pipeline = Create(new ScriptedRecognizer(_ => "x", faultOnRecognize: true));
        pipeline.Faulted += (_, _) => faulted = true;

        await pipeline.StartAsync(LoudChunks(200), "ja", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(faulted);
        Assert.Equal(CaptionPipelineState.Faulted, pipeline.State);
    }

    [Fact]
    public async Task SlowRecognizer_RaisesBackpressureMetric()
    {
        var pipeline = Create(new ScriptedRecognizer(_ => "你好", delayMs: 250), FastOptions());
        await pipeline.StartAsync(LoudChunks(60), "zh", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(pipeline.CurrentMetrics.SkippedCycles > 0,
            $"expected back-pressure skips, got {pipeline.CurrentMetrics.SkippedCycles}");
    }
}
