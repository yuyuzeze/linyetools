using KikuCaption.Core.Models;
using KikuCaption.Audio.Capture;
using KikuCaption.Audio.Wav;
using KikuCaption.Speech.Stabilization;
using KikuCaption.Speech.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// End-to-end real-model tests for the progressive pipeline (Milestone 3). Gated by
/// KIKU_REALMODEL=1; the Chinese tests also need KIKU_ZH_WAV. Uses a synthesized Chinese WAV,
/// so it verifies zh; ja readable text remains "未验证" (no Japanese sample).
/// </summary>
[Trait("Category", "RealModel")]
public class RealtimePipelineIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public RealtimePipelineIntegrationTests(ITestOutputHelper output) => _output = output;

    private static ProgressiveCaptionOptions Options() => new()
    {
        PartialIntervalMs = 700,
        RecentCandidates = 2,
        SilenceFinalMs = 600,
        MaxSentenceSeconds = 12,
        MaxWaitSeconds = 20
    };

    private static bool CjkPresent(string text) => text.Any(c => c >= '一' && c <= '鿿');

    [Fact]
    public async Task Pipeline_RecognizesChineseWav_PartialThenFinal()
    {
        if (!RealModelSupport.Enabled) { _output.WriteLine("[SKIPPED] KIKU_REALMODEL!=1"); return; }
        var wav = RealModelSupport.ChineseWav;
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav)) { _output.WriteLine("[SKIPPED] 无 KIKU_ZH_WAV"); return; }
        var located = RealModelSupport.Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir)) { _output.WriteLine("[SKIPPED] 无 venv/模型"); return; }

        var options = Options();
        await using var pipeline = new RealtimeCaptionPipeline(
            RealModelSupport.RecognizerFactory(located.Value.Options), options,
            new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }), NullLogger<RealtimeCaptionPipeline>.Instance);

        var partials = new List<string>();
        var finals = new List<string>();
        pipeline.PartialUpdated += (_, e) => partials.Add(e.PartialText);
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        await pipeline.StartAsync(WavFileAudioReader.ReadAsync(wav!), "zh", CancellationToken.None);
        await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(90));

        var metrics = pipeline.CurrentMetrics;
        _output.WriteLine($"partials={partials.Count} finals={finals.Count} RTF={metrics.Rtf:0.00} " +
            $"lastInfer={metrics.LastInferenceMs}ms queue={metrics.QueueDepthMs}ms skipped={metrics.SkippedCycles}");
        if (partials.Count > 0) _output.WriteLine("partial example: " + partials[^1]);
        if (finals.Count > 0) _output.WriteLine("final example: " + string.Join(" | ", finals));

        Assert.NotEmpty(partials);
        Assert.NotEmpty(finals);
        Assert.True(CjkPresent(string.Concat(finals)), "final text should contain CJK");
        Assert.Equal(CaptionPipelineState.Stopped, pipeline.State);
    }

    [Fact]
    public async Task Pipeline_LiveLoopback_RecognizesPlayedChinese()
    {
        if (!RealModelSupport.Enabled) { _output.WriteLine("[SKIPPED] KIKU_REALMODEL!=1"); return; }
        var wav = RealModelSupport.ChineseWav;
        if (string.IsNullOrWhiteSpace(wav) || !File.Exists(wav)) { _output.WriteLine("[SKIPPED] 无 KIKU_ZH_WAV"); return; }
        var located = RealModelSupport.Locate();
        if (located is null || !Directory.Exists(located.Value.ModelDir)) { _output.WriteLine("[SKIPPED] 无 venv/模型"); return; }
        try { using var probe = new WasapiLoopbackCapture(); }
        catch (Exception ex) { _output.WriteLine("[SKIPPED] 无音频设备：" + ex.Message); return; }

        var capture = new WasapiLoopbackAudioCaptureService(NullLogger<WasapiLoopbackAudioCaptureService>.Instance);
        await using var pipeline = new RealtimeCaptionPipeline(
            RealModelSupport.RecognizerFactory(located.Value.Options), Options(),
            new SpeechOptionsProvider(new SpeechOptions { Language = "ja" }), NullLogger<RealtimeCaptionPipeline>.Instance);

        var partials = new List<string>();
        var finals = new List<string>();
        pipeline.PartialUpdated += (_, e) => partials.Add(e.PartialText);
        pipeline.FinalProduced += (_, e) => finals.Add(e.Text);

        using var cts = new CancellationTokenSource();
        await pipeline.StartAsync(capture.CaptureAsync(cts.Token), "zh", CancellationToken.None);

        // Play the Chinese WAV to the default endpoint (loopback captures it) a few times.
        for (int i = 0; i < 2; i++)
        {
            using var waveOut = new WaveOutEvent();
            using var reader = new WaveFileReader(wav!);
            waveOut.Init(reader);
            waveOut.Play();
            while (waveOut.PlaybackState == PlaybackState.Playing)
            {
                await Task.Delay(100);
            }
        }

        await Task.Delay(1500); // let the last utterance finalize
        cts.Cancel();
        await pipeline.StopAsync();
        await capture.DisposeAsync();

        _output.WriteLine($"live partials={partials.Count} finals={finals.Count}");
        if (finals.Count > 0) _output.WriteLine("live final: " + string.Join(" | ", finals));

        Assert.NotEmpty(partials);
        if (finals.Count > 0)
        {
            Assert.True(CjkPresent(string.Concat(finals)), "final text should contain CJK");
        }
    }
}
