using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KikuCaption.Audio.Wav;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Export;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

public sealed record PostMeetingCorrectionRequest(
    Guid SessionId,
    string MediaPath,
    string OutputDirectory,
    string Language);

public sealed record CorrectedCaption(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    double? Confidence);

public sealed record PostMeetingCorrectionResult(
    int SegmentCount,
    string JsonPath,
    string TextPath,
    string SrtPath);

public interface IMeetingAudioExtractor
{
    Task ExtractAsync(string mediaPath, string wavPath, CancellationToken cancellationToken);
}

/// <summary>Extracts the complete recording audio as 16 kHz mono PCM without invoking a shell.</summary>
public sealed class FfmpegMeetingAudioExtractor : IMeetingAudioExtractor
{
    private readonly RecordingRuntimeOptions _recording;

    public FfmpegMeetingAudioExtractor(RecordingRuntimeOptions recording) => _recording = recording;

    public async Task ExtractAsync(string mediaPath, string wavPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_recording.FFmpegPath) || !File.Exists(_recording.FFmpegPath))
            throw new InvalidOperationException("FFmpeg is unavailable.");
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Meeting recording was not found.", mediaPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _recording.FFmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var value in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", mediaPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath
        }) startInfo.ArgumentList.Add(value);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(wavPath))
            throw new InvalidOperationException($"FFmpeg audio extraction failed (exit {process.ExitCode}): {stderr.Trim()}");
    }
}

/// <summary>
/// Runs an independent medium/int8 recognition pass after recording has safely stopped. It never
/// changes SQLite or the realtime transcript; output is written to corrected-transcript.* only.
/// </summary>
public sealed class PostMeetingCorrectionService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Func<ISpeechRecognizer> _recognizerFactory;
    private readonly ISpeechOptionsProvider _speechOptionsProvider;
    private readonly IMeetingAudioExtractor _extractor;
    private readonly CorrectionModelLocator _modelLocator;
    private readonly ILogger<PostMeetingCorrectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _activeCts;

    public PostMeetingCorrectionService(
        Func<ISpeechRecognizer> recognizerFactory,
        ISpeechOptionsProvider speechOptionsProvider,
        IMeetingAudioExtractor extractor,
        CorrectionModelLocator modelLocator,
        ILogger<PostMeetingCorrectionService> logger)
    {
        _recognizerFactory = recognizerFactory;
        _speechOptionsProvider = speechOptionsProvider;
        _extractor = extractor;
        _modelLocator = modelLocator;
        _logger = logger;
    }

    public void CancelCurrent() => _activeCts?.Cancel();

    public CorrectionModelAvailability ModelAvailability => _modelLocator.Check();

    public async Task<PostMeetingCorrectionResult> RunAsync(
        PostMeetingCorrectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = linked;
        var tempWav = Path.Combine(request.OutputDirectory, $".correction-{request.SessionId:N}.wav");
        try
        {
            var availability = _modelLocator.Check();
            if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.ModelPath))
                throw new InvalidOperationException("The local faster-whisper medium model is not installed or is incomplete.");

            Directory.CreateDirectory(request.OutputDirectory);
            await _extractor.ExtractAsync(request.MediaPath, tempWav, linked.Token).ConfigureAwait(false);

            var baseOptions = _speechOptionsProvider.ForLanguage(request.Language);
            var options = baseOptions with
            {
                // Pass the complete local snapshot/directory itself. faster-whisper then loads it
                // directly and never attempts a Hugging Face download through the company proxy.
                Model = availability.ModelPath,
                Device = "cpu",
                ComputeType = "int8",
                BeamSize = Math.Max(2, baseOptions.BeamSize),
                InitializeTimeout = TimeSpan.FromMinutes(15)
            };

            var captions = new List<CorrectedCaption>();
            await using (var recognizer = _recognizerFactory())
            {
                await recognizer.InitializeAsync(options, linked.Token).ConfigureAwait(false);
                await foreach (var update in recognizer.RecognizeAsync(
                                   WavFileAudioReader.ReadAsync(tempWav, 320_000, linked.Token), linked.Token)
                                   .ConfigureAwait(false))
                {
                    if (update.Kind != TranscriptUpdateKind.FinalCandidate || string.IsNullOrWhiteSpace(update.Text)) continue;
                    captions.Add(new CorrectedCaption(
                        update.StartTime,
                        update.EndTime < update.StartTime ? update.StartTime : update.EndTime,
                        update.Text.Trim(),
                        update.Confidence));
                }
            }

            var result = await ExportAsync(request, captions, linked.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "Post-meeting correction completed for session {SessionId}: {Count} segments (model medium/int8).",
                request.SessionId, captions.Count);
            return result;
        }
        finally
        {
            _activeCts = null;
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { /* cleanup best effort */ }
            _gate.Release();
        }
    }

    private static async Task<PostMeetingCorrectionResult> ExportAsync(
        PostMeetingCorrectionRequest request,
        IReadOnlyList<CorrectedCaption> captions,
        CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(request.OutputDirectory, "corrected-transcript.json");
        var textPath = Path.Combine(request.OutputDirectory, "corrected-transcript.txt");
        var srtPath = Path.Combine(request.OutputDirectory, "corrected-transcript.srt");
        var json = JsonSerializer.Serialize(new
        {
            sessionId = request.SessionId.ToString("N"),
            language = request.Language,
            model = "medium",
            computeType = "int8",
            createdAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            segments = captions.Select((x, index) => new
            {
                sequenceNumber = index + 1,
                start = Math.Round(x.Start.TotalSeconds, 3),
                end = Math.Round(x.End.TotalSeconds, 3),
                text = x.Text,
                confidence = x.Confidence
            })
        }, JsonOptions);

        var txt = new StringBuilder();
        var srt = new StringBuilder();
        for (var i = 0; i < captions.Count; i++)
        {
            var caption = captions[i];
            txt.Append('[').Append(Clock(caption.Start)).Append("] ").Append(caption.Text).Append('\n');
            srt.Append(i + 1).Append("\r\n")
                .Append(Srt(caption.Start)).Append(" --> ").Append(Srt(caption.End)).Append("\r\n")
                .Append(caption.Text).Append("\r\n\r\n");
        }

        await AtomicFile.WriteAllTextAsync(jsonPath, json, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(textPath, txt.ToString(), cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(srtPath, srt.ToString(), cancellationToken).ConfigureAwait(false);
        return new PostMeetingCorrectionResult(captions.Count, jsonPath, textPath, srtPath);
    }

    private static string Clock(TimeSpan value)
        => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private static string Srt(TimeSpan value)
        => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";

    public ValueTask DisposeAsync()
    {
        CancelCurrent();
        return ValueTask.CompletedTask;
    }
}
