using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

/// <summary>UI-R4B: the dictionary value type validates identically to the shared Hotwords rules.</summary>
public class SpeechDictionaryProfileTests
{
    private static SpeechDictionaryProfile Make(string name = "n", string lang = "ja",
        string? prompt = null, IEnumerable<string>? hotwords = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        LanguageCode = lang,
        InitialPrompt = prompt,
        Hotwords = (hotwords ?? Array.Empty<string>()).ToArray()
    };

    [Fact] // stable, distinct built-in ids per language
    public void BuiltInIds_AreStableAndDistinct()
    {
        Assert.Equal(SpeechDictionaryProfile.BuiltInJapaneseId, SpeechDictionaryProfile.BuiltInIdFor("ja"));
        Assert.Equal(SpeechDictionaryProfile.BuiltInChineseId, SpeechDictionaryProfile.BuiltInIdFor("zh"));
        Assert.NotEqual(SpeechDictionaryProfile.BuiltInJapaneseId, SpeechDictionaryProfile.BuiltInChineseId);
        Assert.Null(SpeechDictionaryProfile.BuiltInIdFor("en"));
    }

    [Fact] // only ja/zh recognition languages are supported (no English recognition)
    public void SupportedLanguages_AreJaAndZhOnly()
    {
        Assert.True(SpeechDictionaryProfile.IsSupportedLanguage("ja"));
        Assert.True(SpeechDictionaryProfile.IsSupportedLanguage("zh"));
        Assert.False(SpeechDictionaryProfile.IsSupportedLanguage("en"));
        Assert.False(SpeechDictionaryProfile.IsSupportedLanguage(null));
    }

    [Fact] // an empty name is rejected
    public void Normalized_RejectsEmptyName()
        => Assert.Throws<ArgumentException>(() => Make(name: "   ").Normalized());

    [Fact] // an unsupported language is rejected
    public void Normalized_RejectsUnsupportedLanguage()
        => Assert.Throws<ArgumentException>(() => Make(lang: "en").Normalized());

    [Fact] // trims name, drops empty/duplicate hotwords, blanks a whitespace prompt
    public void Normalized_CleansFields()
    {
        var p = Make(name: "  会议  ", prompt: "   ", hotwords: new[] { "Azure", "Azure", "", "  OpenAI " }).Normalized();
        Assert.Equal("会议", p.Name);
        Assert.Null(p.InitialPrompt);
        Assert.Equal(new[] { "Azure", "OpenAI" }, p.Hotwords);
    }

    [Fact] // 64 hotwords ok, 65 rejected (shared Hotwords limit)
    public void Normalized_EnforcesHotwordCount()
    {
        var ok = Make(hotwords: Enumerable.Range(0, Hotwords.MaxCount).Select(i => "t" + i)).Normalized();
        Assert.Equal(Hotwords.MaxCount, ok.Hotwords.Count);
        Assert.Throws<ArgumentException>(() => Make(hotwords: Enumerable.Range(0, Hotwords.MaxCount + 1).Select(i => "t" + i)).Normalized());
    }

    [Fact] // a term longer than 40 chars is rejected
    public void Normalized_EnforcesTermLength()
        => Assert.Throws<ArgumentException>(() => Make(hotwords: new[] { new string('x', Hotwords.MaxTermLength + 1) }).Normalized());

    [Fact] // the initial prompt has an explicit upper bound (2000)
    public void Normalized_EnforcesPromptBound()
    {
        var ok = Make(prompt: new string('あ', SpeechDictionaryProfile.MaxInitialPromptLength)).Normalized();
        Assert.NotNull(ok.InitialPrompt);
        Assert.Throws<ArgumentException>(() => Make(prompt: new string('あ', SpeechDictionaryProfile.MaxInitialPromptLength + 1)).Normalized());
    }
}
