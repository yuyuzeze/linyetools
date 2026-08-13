using KikuCaption.App.ViewModels;
using KikuCaption.Infrastructure.Configuration;
using Xunit;

namespace KikuCaption.App.Tests;

public sealed class SubtitleThemeTests
{
    [Theory]
    [InlineData(null, "night-sakura")]
    [InlineData("default", "night-sakura")]
    [InlineData("night-sakura", "deep-sea")]
    [InlineData("deep-sea", "default")]
    [InlineData("unknown", "night-sakura")]
    public void ThemeCycle_IsDeterministic(string? current, string expected)
        => Assert.Equal(expected, SubtitleThemeCycle.Next(current));

    [Theory]
    [InlineData("night-sakura", "#2B1425")]
    [InlineData("deep-sea", "#071D2B")]
    [InlineData("unknown", "#000000")]
    public void Overlay_NormalizesAndAppliesTheme(string theme, string expectedBackground)
    {
        var overlay = new SubtitleOverlayViewModel(new SubtitleSettings());
        overlay.ApplyTheme(theme);
        Assert.Equal(expectedBackground, overlay.ThemeBackgroundColor);
    }
}
