using System.Runtime.CompilerServices;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Speech.SpeechCpp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class SpeechCppSpeechRecognizerTests
{
    private static SpeechCppSpeechRecognizer Create(FakeSpeechCppWorker worker) =>
        new(worker, NullLogger<SpeechCppSpeechRecognizer>.Instance);

    private static SpeechOptions Options(bool timestamps = false, string? keywords = null) => new()
    {
        Engine = SpeechEngine.SpeechCpp,
        Model = "haas-ja-v1.0",
        Language = "ja",
        RequestTimestamps = timestamps,
        Keywords = keywords,
        InitializeTimeout = TimeSpan.FromSeconds(5)
    };

    /// <summary>One AudioChunk of <paramref name="bytes"/> PCM bytes, repeated <paramref name="count"/> times.</summary>
    private static async IAsyncEnumerable<AudioChunk> Audio(int count, int bytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AudioChunk(new byte[bytes], TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Initialize_StartsWorkerWithModel_AndWaitsReady()
    {
        var worker = new FakeSpeechCppWorker();
        await using var recognizer = Create(worker);

        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        Assert.Equal("haas-ja-v1.0", worker.StartedModel);
    }

    [Fact]
    public async Task Initialize_WrongEngine_Throws()
    {
        var worker = new FakeSpeechCppWorker();
        await using var recognizer = Create(worker);

        var options = Options() with { Engine = SpeechEngine.Whisper };
        await Assert.ThrowsAsync<SpeechRecognitionException>(() =>
            recognizer.InitializeAsync(options, CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_Fatal_Throws()
    {
        var worker = new FakeSpeechCppWorker { RespondReady = false, FatalCode = "model_file" };
        await using var recognizer = Create(worker);

        var ex = await Assert.ThrowsAsync<SpeechRecognitionException>(() =>
            recognizer.InitializeAsync(Options(), CancellationToken.None));
        Assert.Equal("model_file", ex.Code);
    }

    [Fact]
    public async Task Recognize_SendsChunksThenTranscribe_AndEmitsSingleFinalCandidate()
    {
        var worker = new FakeSpeechCppWorker { EndText = "こんにちは。" };
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        await foreach (var u in recognizer.RecognizeAsync(Audio(2, 32000), CancellationToken.None))
        {
            updates.Add(u);
        }

        // 2 chunk lines + 1 transcribe line.
        Assert.Equal(2, worker.Sent.Count(m => m.Type == "chunk"));
        var transcribe = Assert.Single(worker.Sent, m => m.Type == "transcribe");
        Assert.Equal(16000, transcribe.SampleRate);

        var update = Assert.Single(updates);
        Assert.Equal(TranscriptUpdateKind.FinalCandidate, update.Kind);
        Assert.Equal("こんにちは。", update.Text);
        // 64000 bytes / 32000 bytes-per-second = 2.0 s of audio.
        Assert.Equal(2.0, update.EndTime.TotalSeconds, 3);
    }

    [Fact]
    public async Task Recognize_WithTimestamps_EmitsOneFinalCandidatePerSegment_AndDropsKeywords()
    {
        var worker = new FakeSpeechCppWorker
        {
            EndSegments = new[]
            {
                new SpeechCppSegment { Start = 0.0, End = 1.2, Text = "まず" },
                new SpeechCppSegment { Start = 1.2, End = 2.4, Text = "次に" }
            }
        };
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(timestamps: true, keywords: "Haas=ハース"), CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        await foreach (var u in recognizer.RecognizeAsync(Audio(1, 32000), CancellationToken.None))
        {
            updates.Add(u);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("まず", updates[0].Text);
        Assert.Equal(1.2, updates[1].StartTime.TotalSeconds, 3);
        Assert.Equal("次に", updates[1].Text);

        var transcribe = Assert.Single(worker.Sent, m => m.Type == "transcribe");
        Assert.True(transcribe.Timestamps);
        Assert.Null(transcribe.Keywords); // Haas: keywords are dropped when timestamps are requested
    }

    [Fact]
    public async Task Recognize_WithKeywordsNoTimestamps_PassesKeywords()
    {
        var worker = new FakeSpeechCppWorker { EndText = "ハース" };
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(keywords: "Haas=ハース"), CancellationToken.None);

        await foreach (var _ in recognizer.RecognizeAsync(Audio(1, 32000), CancellationToken.None)) { }

        var transcribe = Assert.Single(worker.Sent, m => m.Type == "transcribe");
        Assert.Equal("Haas=ハース", transcribe.Keywords);
        Assert.Null(transcribe.Timestamps);
    }

    [Fact]
    public async Task Recognize_WorkerError_Throws()
    {
        var worker = new FakeSpeechCppWorker { TranscribeErrorCode = "out_of_range" };
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        await Assert.ThrowsAsync<SpeechRecognitionException>(async () =>
        {
            await foreach (var _ in recognizer.RecognizeAsync(Audio(1, 32000), CancellationToken.None)) { }
        });
    }
}
