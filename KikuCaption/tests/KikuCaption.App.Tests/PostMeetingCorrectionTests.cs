using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace KikuCaption.App.Tests;

public sealed class PostMeetingCorrectionTests
{
    private sealed class Extractor : IMeetingAudioExtractor
    {
        public Task ExtractAsync(string mediaPath, string wavPath, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
            using var writer = new NAudio.Wave.WaveFileWriter(wavPath, new NAudio.Wave.WaveFormat(16000, 16, 1));
            writer.Write(new byte[3200]);
            return Task.CompletedTask;
        }
    }

    private sealed class Recognizer : ISpeechRecognizer
    {
        public SpeechOptions? Options { get; private set; }

        public Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken)
        {
            Options = options;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken)) { }
            yield return new TranscriptUpdate
            {
                SessionId = Guid.NewGuid(), Kind = TranscriptUpdateKind.FinalCandidate,
                StartTime = TimeSpan.FromSeconds(1.25), EndTime = TimeSpan.FromSeconds(3.5),
                Text = "校正版の字幕です。", Confidence = 0.9, Sequence = 1
            };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Run_UsesMediumInt8_AndWritesSeparateFilesWithoutTouchingRealtimeTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiku-correction", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var realtime = Path.Combine(root, "transcript.srt");
        await File.WriteAllTextAsync(realtime, "REALTIME");
        var media = Path.Combine(root, "meeting.mp4");
        await File.WriteAllBytesAsync(media, new byte[] { 1 });
        var recognizer = new Recognizer();
        var provider = new SpeechOptionsProvider(new SpeechOptions
        {
            Language = "ja", Model = "small", Device = "cpu", ComputeType = "int8", BeamSize = 2
        });
        await using var service = new PostMeetingCorrectionService(
            () => recognizer, provider, new Extractor(), NullLogger<PostMeetingCorrectionService>.Instance);

        try
        {
            var result = await service.RunAsync(new PostMeetingCorrectionRequest(
                Guid.NewGuid(), media, root, "ja"));

            Assert.Equal("medium", recognizer.Options?.Model);
            Assert.Equal("int8", recognizer.Options?.ComputeType);
            Assert.Equal("cpu", recognizer.Options?.Device);
            Assert.Equal("REALTIME", await File.ReadAllTextAsync(realtime));
            Assert.Contains("校正版の字幕です。", await File.ReadAllTextAsync(result.SrtPath));
            Assert.Contains("00:00:01,250 --> 00:00:03,500", await File.ReadAllTextAsync(result.SrtPath));
            Assert.True(File.Exists(result.JsonPath));
            Assert.True(File.Exists(result.TextPath));
            Assert.Empty(Directory.GetFiles(root, ".correction-*.wav"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
