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
}
