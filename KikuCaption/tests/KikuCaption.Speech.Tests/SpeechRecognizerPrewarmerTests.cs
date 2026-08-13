using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class SpeechRecognizerPrewarmerTests
{
    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public int Initialized { get; private set; }
        public int Disposed { get; private set; }
        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken) { Initialized++; return Task.CompletedTask; }
        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task MatchingPrewarm_IsTransferredWithoutSecondInitialization()
    {
        var fake = new FakeRecognizer();
        await using var subject = new SpeechRecognizerPrewarmer(() => fake, NullLogger<SpeechRecognizerPrewarmer>.Instance);
        var options = new SpeechOptions { Language = "ja" };
        await subject.PrewarmAsync(options);
        var acquired = await subject.AcquireAsync(options, CancellationToken.None);
        Assert.Same(fake, acquired);
        Assert.Equal(1, fake.Initialized);
        Assert.Equal(0, fake.Disposed);
        await acquired.DisposeAsync();
    }

    [Fact]
    public async Task DisableOrExit_ReleasesCachedRecognizer_Idempotently()
    {
        var fake = new FakeRecognizer();
        var subject = new SpeechRecognizerPrewarmer(() => fake, NullLogger<SpeechRecognizerPrewarmer>.Instance);
        await subject.PrewarmAsync(new SpeechOptions { Language = "ja" });
        await subject.ClearAsync();
        Assert.Equal(1, fake.Disposed);
        await subject.DisposeAsync();
        await subject.DisposeAsync();
        Assert.Equal(1, fake.Disposed);
    }
}
