using KikuCaption.Speech.Stabilization;
using Xunit;

namespace KikuCaption.Speech.Tests;

public class ProgressiveCaptionOptionsTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        new ProgressiveCaptionOptions().Validate();
    }

    [Theory]
    [InlineData(nameof(ProgressiveCaptionOptions.PartialIntervalMs))]
    [InlineData(nameof(ProgressiveCaptionOptions.WindowSeconds))]
    [InlineData(nameof(ProgressiveCaptionOptions.OverlapSeconds))]
    [InlineData(nameof(ProgressiveCaptionOptions.RecentCandidates))]
    [InlineData(nameof(ProgressiveCaptionOptions.SilenceFinalMs))]
    [InlineData(nameof(ProgressiveCaptionOptions.MaxLines))]
    public void OutOfRange_Throws(string field)
    {
        var options = field switch
        {
            nameof(ProgressiveCaptionOptions.PartialIntervalMs) => new ProgressiveCaptionOptions { PartialIntervalMs = 100 },
            nameof(ProgressiveCaptionOptions.WindowSeconds) => new ProgressiveCaptionOptions { WindowSeconds = 10 },
            nameof(ProgressiveCaptionOptions.OverlapSeconds) => new ProgressiveCaptionOptions { OverlapSeconds = 5 },
            nameof(ProgressiveCaptionOptions.RecentCandidates) => new ProgressiveCaptionOptions { RecentCandidates = 9 },
            nameof(ProgressiveCaptionOptions.SilenceFinalMs) => new ProgressiveCaptionOptions { SilenceFinalMs = 50 },
            _ => new ProgressiveCaptionOptions { MaxLines = 99 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void MaxSentenceBelowWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProgressiveCaptionOptions { WindowSeconds = 6, MaxSentenceSeconds = 3 }.Validate());
    }

    [Fact] // Data-loss Hotfix: the experimental sliding-window path must never be enabled
    public void ExperimentalSlidingWindow_Enabled_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            new ProgressiveCaptionOptions { UseExperimentalSlidingWindow = true }.Validate());
    }

    [Fact]
    public void ExperimentalSlidingWindow_DefaultsFalse()
    {
        Assert.False(new ProgressiveCaptionOptions().UseExperimentalSlidingWindow);
    }

    [Fact] // Data-loss Hotfix: recommended defaults are restored to the pre-sliding-window values
    public void RestoredDefaults_MatchHotfixRecommendation()
    {
        var o = new ProgressiveCaptionOptions();
        Assert.Equal(700, o.SilenceFinalMs);
        Assert.Equal(2, o.StableRepeatCount);
        Assert.Equal(12, o.MaxSentenceSeconds);
        Assert.Equal(20, o.MaxWaitSeconds);
    }
}
