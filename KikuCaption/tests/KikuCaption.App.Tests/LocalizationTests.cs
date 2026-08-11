using System.Linq;
using KikuCaption.App.Localization;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R3 localization: resource completeness, language switching, and safe fallback.</summary>
public class LocalizationTests
{
    [Fact] // required: every culture's table must contain exactly the same keys (zh-CN / en-US / ja-JP)
    public void ResourceKeys_AreIdenticalAcrossCultures()
    {
        var reference = LocalizedStrings.Tables[LocalizedStrings.ZhCN].Keys.ToHashSet();

        foreach (var culture in LocalizedStrings.SupportedCultures)
        {
            var keys = LocalizedStrings.Tables[culture].Keys.ToHashSet();
            var missing = reference.Except(keys).ToList();
            var extra = keys.Except(reference).ToList();

            Assert.True(missing.Count == 0, $"{culture} is missing: " + string.Join(", ", missing));
            Assert.True(extra.Count == 0, $"{culture} has extra: " + string.Join(", ", extra));
        }
    }

    [Fact] // all three cultures are registered
    public void ThreeCultures_AreSupported()
    {
        Assert.Equal(3, LocalizedStrings.SupportedCultures.Count);
        Assert.Contains(LocalizedStrings.JaJP, LocalizedStrings.SupportedCultures);
        Assert.True(LocalizedStrings.Tables.ContainsKey(LocalizedStrings.JaJP));
    }

    [Fact] // no value is left empty in either table
    public void AllValues_AreNonEmpty()
    {
        foreach (var (_, table) in LocalizedStrings.Tables)
        {
            Assert.All(table.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }
    }

    [Fact]
    public void SwitchingLanguage_ChangesResolvedText()
    {
        var loc = new LocalizationService();
        Assert.Equal(LocalizedStrings.ZhCN, loc.CurrentLanguage);
        Assert.Equal("首页", loc["Nav.Home"]);

        loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal(LocalizedStrings.EnUS, loc.CurrentLanguage);
        Assert.Equal("Home", loc["Nav.Home"]);

        loc.SetLanguage(LocalizedStrings.ZhCN);
        Assert.Equal("首页", loc["Nav.Home"]);
    }

    [Fact]
    public void SwitchingLanguage_RaisesIndexerChange()
    {
        var loc = new LocalizationService();
        var raised = false;
        loc.PropertyChanged += (_, e) => { if (e.PropertyName is "Item[]" or "CurrentLanguage") raised = true; };

        loc.SetLanguage(LocalizedStrings.EnUS);

        Assert.True(raised);
    }

    [Fact]
    public void UnknownKey_FallsBackToTheKey_NeverThrows()
    {
        var loc = new LocalizationService();
        Assert.Equal("No.Such.Key", loc["No.Such.Key"]);
    }

    [Fact]
    public void UnknownOrSameCulture_IsNoOp()
    {
        var loc = new LocalizationService();
        loc.SetLanguage("fr-FR");        // unknown
        Assert.Equal(LocalizedStrings.ZhCN, loc.CurrentLanguage);
        loc.SetLanguage(LocalizedStrings.ZhCN); // already current
        Assert.Equal(LocalizedStrings.ZhCN, loc.CurrentLanguage);
    }
}
