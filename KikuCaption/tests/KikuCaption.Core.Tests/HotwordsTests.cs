using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

public class HotwordsTests
{
    [Fact] // trims, drops empties, de-duplicates
    public void Normalize_TrimsAndDeduplicates()
    {
        var result = Hotwords.Normalize(new[] { " Azure ", "Azure", "", "  ", "OpenAI" });
        Assert.Equal(new[] { "Azure", "OpenAI" }, result);
    }

    [Fact] // test 5 (compat): null / empty glossary is fine
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(Hotwords.Normalize(null));
        Assert.Empty(Hotwords.Normalize(System.Array.Empty<string>()));
        Assert.Null(Hotwords.ToWireString(Hotwords.Normalize(null)));
    }

    [Fact] // test 6: an oversized glossary is rejected (too many entries)
    public void Normalize_TooManyEntries_Throws()
    {
        var many = Enumerable.Range(0, Hotwords.MaxCount + 5).Select(i => "term" + i).ToArray();
        Assert.Throws<ArgumentException>(() => Hotwords.Normalize(many));
    }

    [Fact] // test 6: a single over-long term is rejected
    public void Normalize_OverlongTerm_Throws()
    {
        Assert.Throws<ArgumentException>(() => Hotwords.Normalize(new[] { new string('あ', Hotwords.MaxTermLength + 1) }));
    }

    [Fact] // test 6: exceeding the total-character budget is rejected
    public void Normalize_TooManyTotalChars_Throws()
    {
        // Each 30 chars; enough of them to blow past MaxTotalCharacters while staying under MaxCount.
        var big = Enumerable.Range(0, 40).Select(i => new string('x', 30) + i).ToArray();
        Assert.Throws<ArgumentException>(() => Hotwords.Normalize(big));
    }

    [Fact] // joins to the single space-separated string faster-whisper expects
    public void ToWireString_JoinsWithSpaces()
    {
        Assert.Equal("Azure OpenAI API", Hotwords.ToWireString(new[] { "Azure", "OpenAI", "API" }));
    }
}
