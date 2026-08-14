using KikuCaption.App.ViewModels;
using Xunit;

namespace KikuCaption.App.Tests;

public sealed class EasterEggClickTrackerTests
{
    [Fact]
    public void FiveQuickClicks_TriggersOnceAndResets()
    {
        var tracker = new EasterEggClickTracker();
        var start = DateTimeOffset.UtcNow;

        for (var index = 0; index < 4; index++)
        {
            Assert.False(tracker.Register(start.AddMilliseconds(index * 100)));
        }

        Assert.True(tracker.Register(start.AddMilliseconds(400)));
        Assert.False(tracker.Register(start.AddMilliseconds(500)));
    }

    [Fact]
    public void LongGap_RestartsTheSequence()
    {
        var tracker = new EasterEggClickTracker();
        var start = DateTimeOffset.UtcNow;

        for (var index = 0; index < 4; index++)
        {
            Assert.False(tracker.Register(start.AddMilliseconds(index * 100)));
        }

        Assert.False(tracker.Register(start.AddSeconds(3)));
    }
}
