using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.SpeechCpp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class SpeechRecognizerFactoryTests
{
    private sealed class StubRecognizer : ISpeechRecognizer
    {
        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Env : ISpeechCppEnvironment
    {
        public bool IsAvailable { get; init; }
    }

    private static SpeechRecognizerFactory Create(ISpeechCppEnvironment env, StubRecognizer whisper, StubRecognizer speechCpp) =>
        new(() => whisper, () => speechCpp, env, NullLogger<SpeechRecognizerFactory>.Instance);

    [Fact]
    public void Whisper_Engine_ReturnsWhisper()
    {
        var whisper = new StubRecognizer();
        var speechCpp = new StubRecognizer();
        var factory = Create(new Env { IsAvailable = true }, whisper, speechCpp);

        var result = factory.Create(new SpeechOptions { Language = "ja", Engine = SpeechEngine.Whisper });

        Assert.Same(whisper, result);
    }

    [Fact]
    public void SpeechCpp_WhenAvailable_ReturnsSpeechCpp()
    {
        var whisper = new StubRecognizer();
        var speechCpp = new StubRecognizer();
        var factory = Create(new Env { IsAvailable = true }, whisper, speechCpp);

        var result = factory.Create(new SpeechOptions { Language = "ja", Engine = SpeechEngine.SpeechCpp });

        Assert.Same(speechCpp, result);
    }

    [Fact]
    public void SpeechCpp_WhenUnavailable_FallsBackToWhisper()
    {
        var whisper = new StubRecognizer();
        var speechCpp = new StubRecognizer();
        var factory = Create(new Env { IsAvailable = false }, whisper, speechCpp);

        var result = factory.Create(new SpeechOptions { Language = "ja", Engine = SpeechEngine.SpeechCpp });

        Assert.Same(whisper, result);
    }
}
