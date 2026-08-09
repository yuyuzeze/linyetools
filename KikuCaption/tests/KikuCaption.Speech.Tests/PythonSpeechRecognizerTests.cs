using System.Runtime.CompilerServices;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class PythonSpeechRecognizerTests
{
    private static PythonSpeechRecognizer Create(FakeWhisperWorker worker) =>
        new(worker, NullLogger<PythonSpeechRecognizer>.Instance);

    private static SpeechOptions Options(string language = "ja", int timeoutMs = 5000) => new()
    {
        Language = language,
        InitializeTimeout = TimeSpan.FromMilliseconds(timeoutMs)
    };

    private static async IAsyncEnumerable<AudioChunk> Audio(int chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AudioChunk(new byte[320], TimeSpan.FromMilliseconds(i * 10), TimeSpan.FromMilliseconds(10));
        }
    }

    [Fact]
    public async Task Initialize_Success_SendsInitializeWithLanguage_ModelLoadedOnce()
    {
        var worker = new FakeWhisperWorker();
        await using var recognizer = Create(worker);

        await recognizer.InitializeAsync(Options("zh"), CancellationToken.None);

        var initialize = Assert.Single(worker.Sent, m => m.Type == ProtocolConstants.Types.Initialize);
        Assert.Equal("zh", initialize.Language);
        Assert.Equal(1, worker.InitializeCount);
    }

    [Fact]
    public async Task Initialize_Twice_Throws()
    {
        var worker = new FakeWhisperWorker();
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => recognizer.InitializeAsync(Options(), CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_WorkerError_ThrowsSpeechException()
    {
        var worker = new FakeWhisperWorker { InitErrorCode = "model_load_failed" };
        await using var recognizer = Create(worker);

        var ex = await Assert.ThrowsAsync<SpeechRecognitionException>(
            () => recognizer.InitializeAsync(Options(), CancellationToken.None));
        Assert.Equal("model_load_failed", ex.Code);
    }

    [Fact]
    public async Task Initialize_NoReady_TimesOut()
    {
        var worker = new FakeWhisperWorker { RespondReady = false };
        await using var recognizer = Create(worker);

        var ex = await Assert.ThrowsAsync<SpeechRecognitionException>(
            () => recognizer.InitializeAsync(Options(timeoutMs: 200), CancellationToken.None));
        Assert.Equal("timeout", ex.Code);
    }

    [Fact]
    public async Task Recognize_EmitsPartialsThenFinals_InOrder()
    {
        var worker = new FakeWhisperWorker();
        worker.Finals.Add((0.0, 1.0, "hello", 0.9));
        worker.Finals.Add((1.0, 2.0, "world", 0.8));
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        var updates = new List<TranscriptUpdate>();
        await foreach (var update in recognizer.RecognizeAsync(Audio(3), CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Equal(
            new[]
            {
                TranscriptUpdateKind.Partial, TranscriptUpdateKind.Partial,
                TranscriptUpdateKind.FinalCandidate, TranscriptUpdateKind.FinalCandidate
            },
            updates.Select(u => u.Kind));

        var finals = updates.Where(u => u.Kind == TranscriptUpdateKind.FinalCandidate).ToList();
        Assert.Equal("hello", finals[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), finals[1].StartTime);
        Assert.Equal(1, worker.InitializeCount);
    }

    [Fact]
    public async Task Recognize_MultipleTimes_ReusesSingleModelLoad()
    {
        var worker = new FakeWhisperWorker();
        worker.Finals.Add((0.0, 1.0, "one", 0.9));
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        await foreach (var _ in recognizer.RecognizeAsync(Audio(2), CancellationToken.None)) { }
        await foreach (var _ in recognizer.RecognizeAsync(Audio(2), CancellationToken.None)) { }

        Assert.Equal(1, worker.InitializeCount);
    }

    [Fact]
    public async Task Recognize_ForwardsAudioAndFlush()
    {
        var worker = new FakeWhisperWorker();
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        await foreach (var _ in recognizer.RecognizeAsync(Audio(3), CancellationToken.None)) { }

        Assert.Equal(3, worker.Sent.Count(m => m.Type == ProtocolConstants.Types.Audio));
        Assert.Contains(worker.Sent, m => m.Type == ProtocolConstants.Types.Flush);
    }

    [Fact]
    public async Task Recognize_Cancelled_Throws()
    {
        var worker = new FakeWhisperWorker { Finals = { (0, 1, "x", null) } };
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in recognizer.RecognizeAsync(Audio(5, cts.Token), cts.Token)) { }
        });
    }

    [Fact]
    public async Task Recognize_AfterWorkerExit_ThrowsSpeechException()
    {
        var worker = new FakeWhisperWorker();
        await using var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        worker.SimulateExit(1);
        await Task.Delay(150); // let the read loop observe the exit

        await Assert.ThrowsAsync<SpeechRecognitionException>(async () =>
        {
            await foreach (var _ in recognizer.RecognizeAsync(Audio(1), CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Dispose_SendsShutdown_AndDisposesWorker()
    {
        var worker = new FakeWhisperWorker();
        var recognizer = Create(worker);
        await recognizer.InitializeAsync(Options(), CancellationToken.None);

        await recognizer.DisposeAsync();

        Assert.Contains(worker.Sent, m => m.Type == ProtocolConstants.Types.Shutdown);
        Assert.True(worker.Disposed);
    }
}
