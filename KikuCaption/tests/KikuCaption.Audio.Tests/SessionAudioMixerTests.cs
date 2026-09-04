using System.Runtime.CompilerServices;
using KikuCaption.Audio.Mixing;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Audio.Tests;

/// <summary>UI-R5A: the session mixer fans mixed PCM out to two isolated consumers and tears down safely.</summary>
public class SessionAudioMixerTests
{
    // A fake capture source that emits constant-valued 20 ms chunks until cancelled.
    private sealed class FakeSource : IAudioCaptureService
    {
        private readonly short _value;
        public FakeSource(short value) => _value = value;

        public async IAsyncEnumerable<AudioChunk> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                yield return Chunk(_value);
                try { await Task.Delay(10, ct); } catch (OperationCanceledException) { yield break; }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static AudioChunk Chunk(short value)
        {
            int samples = AudioMixTimeline.SampleRate * 20 / 1000;
            var pcm = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }
            return new AudioChunk(pcm, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        }
    }

    private static short FirstSample(byte[] pcm) => (short)(pcm[0] | (pcm[1] << 8));

    private static async Task<(long bytes, short lastValue)> DrainAsync(IAudioCaptureService source)
    {
        long bytes = 0;
        short last = 0;
        await foreach (var chunk in source.CaptureAsync(CancellationToken.None))
        {
            bytes += chunk.Pcm.Length;
            if (chunk.Pcm.Length >= 2) { last = FirstSample(chunk.Pcm.ToArray()); }
        }
        return (bytes, last);
    }

    [Fact] // constructing with no inputs is rejected
    public void NoInputs_Throws()
        => Assert.Throws<ArgumentException>(() => new SessionAudioMixer(null, null, NullLogger.Instance));

    [Fact] // scenario 18: the SAME mixed PCM reaches both the caption and recording branches
    public async Task FansOutMixToBothConsumers()
    {
        var mixer = new SessionAudioMixer(new FakeSource(1000), new FakeSource(1500), NullLogger.Instance);
        var speechSource = mixer.CreateSpeechSource();
        var recording = mixer.CreateRecordingSource();
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        var speechTask = DrainAsync(speechSource);
        var recTask = DrainAsync(recording);

        await Task.Delay(250);
        cts.Cancel();
        await mixer.StopAsync();

        var speech = await speechTask;
        var rec = await recTask;

        Assert.True(speech.bytes > 0, "speech branch received no audio");
        Assert.True(rec.bytes > 0, "recording branch received no audio");
        Assert.Equal(2500, speech.lastValue); // system(1000) + mic(1500), saturating add
        Assert.Equal(2500, rec.lastValue);
    }

    [Fact] // scenario 6/7: single-input sessions run (system-only, mic-only)
    public async Task SingleInput_Runs()
    {
        var sysOnly = new SessionAudioMixer(new FakeSource(800), null, NullLogger.Instance);
        var speech = sysOnly.CreateSpeechSource();
        using var cts = new CancellationTokenSource();
        sysOnly.Start(cts.Token);
        var task = DrainAsync(speech);
        await Task.Delay(150);
        cts.Cancel();
        await sysOnly.StopAsync();
        var r = await task;
        Assert.True(r.bytes > 0);
        Assert.Equal(800, r.lastValue);
    }

    [Fact] // scenario 14: a consumer that never reads its branch does not block the other consumer
    public async Task UnreadConsumer_DoesNotBlockOther()
    {
        var mixer = new SessionAudioMixer(new FakeSource(1000), null, NullLogger.Instance);
        var speechSource = mixer.CreateSpeechSource();
        _ = mixer.CreateRecordingSource(); // created but never drained
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        var speechTask = DrainAsync(speechSource);
        await Task.Delay(200);
        cts.Cancel();
        await mixer.StopAsync();

        var speech = await speechTask;
        Assert.True(speech.bytes > 0, "an unread recording branch starved the speech branch");
    }

    [Fact] // scenario 15: cancel + repeated stop + dispose are all safe
    public async Task Cancel_RepeatStop_Dispose_AreSafe()
    {
        var mixer = new SessionAudioMixer(new FakeSource(1000), new FakeSource(500), NullLogger.Instance);
        var speech = mixer.CreateSpeechSource();
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);
        _ = DrainAsync(speech);
        await Task.Delay(80);

        cts.Cancel();
        await mixer.StopAsync();
        await mixer.StopAsync();          // idempotent
        await mixer.DisposeAsync();       // no throw
    }

    // ---- UI-R6A: recording-only mode never creates the speech branch --------------------------

    [Fact] // R6A: recording-only → no speech source, zero speech-branch, zero speechDropped, rec gets full mix
    public async Task RecordingOnly_NoSpeechBranch_NoDrops_RecordingGetsFullMix()
    {
        var mixer = new SessionAudioMixer(new FakeSource(1200), null, NullLogger.Instance);
        var recording = mixer.CreateRecordingSource(); // ONLY the recording consumer is registered
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        Assert.Null(mixer.SpeechSource); // the speech branch was never created

        var recTask = DrainAsync(recording);
        await Task.Delay(250);
        cts.Cancel();
        await mixer.StopAsync();

        var rec = await recTask;
        Assert.True(rec.bytes > 0, "recording branch received no audio");
        Assert.Equal(1200, rec.lastValue);            // full mixed PCM reached the recorder
        Assert.Equal(0, mixer.SpeechDroppedChunks);   // nothing was ever written to a speech branch
    }

    [Fact] // R6A: a never-created speech branch cannot slow or starve the recording consumer over time
    public async Task RecordingOnly_LongRun_RecordingStaysHealthy()
    {
        var mixer = new SessionAudioMixer(new FakeSource(900), new FakeSource(300), NullLogger.Instance);
        var recording = mixer.CreateRecordingSource();
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        var recTask = DrainAsync(recording);
        await Task.Delay(600); // several branch-capacity windows
        cts.Cancel();
        await mixer.StopAsync();

        var rec = await recTask;
        Assert.True(rec.bytes > 0);
        Assert.Equal(1200, rec.lastValue);          // sys(900) + mic(300)
        Assert.Equal(0, mixer.SpeechDroppedChunks); // no phantom speech branch, no drops
    }

    [Fact] // R6A: registering a consumer after Start is rejected (branches are fixed at Start)
    public void RegisteringConsumerAfterStart_Throws()
    {
        var mixer = new SessionAudioMixer(new FakeSource(1000), null, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        Assert.Throws<InvalidOperationException>(() => mixer.CreateSpeechSource());
        Assert.Throws<InvalidOperationException>(() => mixer.CreateRecordingSource());
    }

    [Fact] // R6A: speech-enabled meeting → both branches receive identical mixed PCM
    public async Task SpeechEnabled_BothBranchesReceiveIdenticalPcm()
    {
        var mixer = new SessionAudioMixer(new FakeSource(700), new FakeSource(200), NullLogger.Instance);
        var speech = mixer.CreateSpeechSource();
        var recording = mixer.CreateRecordingSource();
        using var cts = new CancellationTokenSource();
        mixer.Start(cts.Token);

        var speechTask = DrainAsync(speech);
        var recTask = DrainAsync(recording);
        await Task.Delay(250);
        cts.Cancel();
        await mixer.StopAsync();

        var s = await speechTask;
        var r = await recTask;
        Assert.True(s.bytes > 0 && r.bytes > 0);
        Assert.Equal(s.lastValue, r.lastValue); // identical mix on both branches
        Assert.Equal(900, s.lastValue);         // sys(700) + mic(200)
    }
}
