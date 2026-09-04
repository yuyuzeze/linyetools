using KikuCaption.ComponentManagement.Versioning;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class ManifestVersionTests
{
    [Fact] // R7A: the classic lexicographic trap — 1.0.10 must be GREATER than 1.0.9
    public void Patch10_IsGreaterThan_Patch9()
    {
        Assert.True(ManifestVersion.Parse("1.0.10") > ManifestVersion.Parse("1.0.9"));
        Assert.True(ManifestVersion.Parse("0.1.10") > ManifestVersion.Parse("0.1.9"));
        Assert.False(ManifestVersion.Parse("1.0.9") > ManifestVersion.Parse("1.0.10"));
    }

    [Theory]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.2.0", "1.1.99")]
    [InlineData("1.0.0.1", "1.0.0.0")]
    [InlineData("10", "9")]
    public void Ordering_IsNumericFieldByField(string bigger, string smaller)
        => Assert.True(ManifestVersion.Parse(bigger) > ManifestVersion.Parse(smaller));

    [Fact] // missing trailing fields are zero → 1.0 == 1.0.0
    public void MissingTrailingFields_AreZero()
    {
        Assert.True(ManifestVersion.Parse("1.0") == ManifestVersion.Parse("1.0.0"));
        Assert.True(ManifestVersion.Parse("1") == ManifestVersion.Parse("1.0.0.0"));
    }

    [Theory] // non-numeric, negative, empty and over-long forms are rejected
    [InlineData("1.0.x")]
    [InlineData("1.-1.0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.0.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-beta")]
    public void InvalidVersions_AreRejected(string text)
        => Assert.False(ManifestVersion.TryParse(text, out _));
}
