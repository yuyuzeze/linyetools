using KikuCaption.Core.Session;
using Xunit;

namespace KikuCaption.Core.Tests;

public class PreflightEvaluatorTests
{
    private static PreflightInputs AllGood() => new()
    {
        DotNetOk = true, PythonOk = true, WhisperDepsOk = true, ModelOk = true, SqliteOk = true,
        WasapiDeviceOk = true, OutputWritable = true, DiskOk = true, FreeDiskGb = 20, RequiredDiskGb = 2,
        FfmpegOk = true, FfprobeOk = true, EncoderOk = true, CaptureTargetOk = true,
        TranslationEnabled = false, TranslationConfigOk = false, DpapiKeyReadable = false
    };

    [Fact] // 12: all good → no blocking, recording available
    public void AllGood_NoBlocking()
    {
        var r = PreflightEvaluator.Evaluate(AllGood());
        Assert.False(r.HasBlocking);
        Assert.True(r.RecordingAvailable);
        Assert.False(r.TranslationAvailable); // translation disabled
    }

    [Theory] // audio / model / storage / output / disk missing → blocking
    [InlineData("audio")]
    [InlineData("model")]
    [InlineData("sqlite")]
    [InlineData("output")]
    [InlineData("disk")]
    [InlineData("python")]
    public void MissingRequired_Blocks(string which)
    {
        var i = AllGood();
        i = which switch
        {
            "audio" => i with { WasapiDeviceOk = false },
            "model" => i with { ModelOk = false },
            "sqlite" => i with { SqliteOk = false },
            "output" => i with { OutputWritable = false },
            "disk" => i with { DiskOk = false },
            "python" => i with { PythonOk = false },
            _ => i
        };

        Assert.True(PreflightEvaluator.Evaluate(i).HasBlocking);
    }

    [Fact] // recording unavailable → WARN not block, RecordingAvailable=false (explicit choice)
    public void RecordingMissing_WarnsNotBlocks()
    {
        var r = PreflightEvaluator.Evaluate(AllGood() with { FfmpegOk = false, FfprobeOk = false });
        Assert.False(r.HasBlocking);       // caption session can still start
        Assert.True(r.HasWarnings);
        Assert.False(r.RecordingAvailable); // UI must offer an explicit choice
    }

    [Fact] // capture target invalid → warn, recording unavailable
    public void CaptureTargetInvalid_Warns()
    {
        var r = PreflightEvaluator.Evaluate(AllGood() with { CaptureTargetOk = false });
        Assert.False(r.HasBlocking);
        Assert.False(r.RecordingAvailable);
    }

    [Fact] // translation enabled but misconfigured → warn, original-only, not blocking
    public void TranslationMisconfigured_Warns_OriginalOnly()
    {
        var r = PreflightEvaluator.Evaluate(AllGood() with { TranslationEnabled = true, TranslationConfigOk = false, DpapiKeyReadable = false });
        Assert.False(r.HasBlocking);
        Assert.True(r.HasWarnings);
        Assert.False(r.TranslationAvailable);
    }

    [Fact] // translation enabled + configured → available
    public void TranslationConfigured_Available()
    {
        var r = PreflightEvaluator.Evaluate(AllGood() with { TranslationEnabled = true, TranslationConfigOk = true, DpapiKeyReadable = true });
        Assert.False(r.HasBlocking);
        Assert.True(r.TranslationAvailable);
    }

    // ---- UI-R6A: recording-only (speech recognition off) --------------------------------------

    [Fact] // R6A-11: missing python/worker/model does NOT block when recognition is off
    public void SpeechOff_MissingSpeechDeps_DoesNotBlock()
    {
        var i = AllGood() with
        {
            SpeechRecognitionRequested = false,
            PythonOk = false, WhisperDepsOk = false, ModelOk = false
        };

        var r = PreflightEvaluator.Evaluate(i);

        Assert.False(r.HasBlocking);        // recording-only meeting can still start
        Assert.True(r.RecordingAvailable);  // recording deps are all present
        // The speech deps are reported as Skip (neutral), never Block.
        Assert.Contains(r.Checks, c => c.Name.Contains("Python") && c.Severity == PreflightSeverity.Skip);
        Assert.Contains(r.Checks, c => c.Name.Contains("Whisper") && c.Severity == PreflightSeverity.Skip);
        Assert.DoesNotContain(r.Checks, c => c.Severity == PreflightSeverity.Block);
    }

    [Fact] // R6A-12: missing FFmpeg STILL blocks recording even when recognition is off
    public void SpeechOff_MissingFfmpeg_RecordingUnavailable()
    {
        var i = AllGood() with { SpeechRecognitionRequested = false, FfmpegOk = false, FfprobeOk = false };
        var r = PreflightEvaluator.Evaluate(i);

        Assert.False(r.RecordingAvailable); // the only output was recording → now unavailable
        Assert.True(r.HasWarnings);
    }

    [Fact] // R6A: with recognition ON (default), missing speech deps block as before
    public void SpeechOn_MissingModel_StillBlocks()
    {
        var r = PreflightEvaluator.Evaluate(AllGood() with { ModelOk = false }); // SpeechRecognitionRequested defaults true
        Assert.True(r.HasBlocking);
    }
}
