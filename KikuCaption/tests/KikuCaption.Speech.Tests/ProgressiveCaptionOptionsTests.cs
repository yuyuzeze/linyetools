using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class ProgressiveCaptionOptionsTests
{
    [Fact] public void Defaults_AreValid() => new ProgressiveCaptionOptions().Validate();

    [Theory]
    [InlineData(nameof(ProgressiveCaptionOptions.PartialIntervalMs))]
    [InlineData(nameof(ProgressiveCaptionOptions.RecentCandidates))]
    [InlineData(nameof(ProgressiveCaptionOptions.SilenceFinalMs))]
    [InlineData(nameof(ProgressiveCaptionOptions.MaxLines))]
    public void OutOfRange_Throws(string field)
    {
        var options = field switch
        {
            nameof(ProgressiveCaptionOptions.PartialIntervalMs) => new ProgressiveCaptionOptions { PartialIntervalMs = 100 },
            nameof(ProgressiveCaptionOptions.RecentCandidates) => new ProgressiveCaptionOptions { RecentCandidates = 9 },
            nameof(ProgressiveCaptionOptions.SilenceFinalMs) => new ProgressiveCaptionOptions { SilenceFinalMs = 50 },
            _ => new ProgressiveCaptionOptions { MaxLines = 99 }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact] public void OneOverlayLine_IsValid() => new ProgressiveCaptionOptions { MaxLines = 1 }.Validate();
}
