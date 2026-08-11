using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

public class SpeechOptionsProviderTests
{
    private static SpeechOptionsProvider Provider() => new(
        new SpeechOptions { Model = "small", Device = "cpu", ComputeType = "int8", BeamSize = 2, Language = "ja", ModelCacheDirectory = @"C:\m" },
        new Dictionary<string, SpeechContext>
        {
            ["ja"] = new("日本語の技術会議", new[] { "Azure", "ファイル検索" }),
            ["zh"] = new("这是技术会议", new[] { "Azure", "OpenAI" }),
        });

    [Fact] // test 1: ja gets the Japanese prompt + Japanese hotwords
    public void Ja_GetsJapaneseContext()
    {
        var o = Provider().ForLanguage("ja");
        Assert.Equal("ja", o.Language);
        Assert.Equal("日本語の技術会議", o.InitialPrompt);
        Assert.Equal(new[] { "Azure", "ファイル検索" }, o.Hotwords);
    }

    [Fact] // test 2: zh never receives the Japanese prompt
    public void Zh_DoesNotGetJapanesePrompt()
    {
        var o = Provider().ForLanguage("zh");
        Assert.NotEqual("日本語の技術会議", o.InitialPrompt);
        Assert.DoesNotContain("ファイル検索", o.Hotwords!);
    }

    [Fact] // test 3: zh gets its own Chinese context
    public void Zh_GetsChineseContext()
    {
        var o = Provider().ForLanguage("zh");
        Assert.Equal("zh", o.Language);
        Assert.Equal("这是技术会议", o.InitialPrompt);
        Assert.Equal(new[] { "Azure", "OpenAI" }, o.Hotwords);
    }

    [Fact] // test 4: an unconfigured language safely gets no prompt/hotwords
    public void UnknownLanguage_GetsNoContext()
    {
        var o = Provider().ForLanguage("en");
        Assert.Equal("en", o.Language);
        Assert.Null(o.InitialPrompt);
        Assert.Null(o.Hotwords);
        Assert.Equal("small", o.Model); // base still applied
    }

    [Fact] // test 5: base model/compute/beam are identical regardless of language (single source)
    public void BaseOptions_ConsistentAcrossLanguages()
    {
        var p = Provider();
        var ja = p.ForLanguage("ja");
        var zh = p.ForLanguage("zh");
        Assert.Equal(ja.Model, zh.Model);
        Assert.Equal(ja.ComputeType, zh.ComputeType);
        Assert.Equal(ja.BeamSize, zh.BeamSize);
        Assert.Equal(ja.ModelCacheDirectory, zh.ModelCacheDirectory);
        Assert.Equal(2, ja.BeamSize);
    }

    [Fact] // empty/whitespace prompt becomes null; empty hotwords become null
    public void EmptyContext_NormalizesToNull()
    {
        var p = new SpeechOptionsProvider(
            new SpeechOptions { Language = "ja" },
            new Dictionary<string, SpeechContext> { ["ja"] = new("   ", System.Array.Empty<string>()) });
        var o = p.ForLanguage("ja");
        Assert.Null(o.InitialPrompt);
        Assert.Null(o.Hotwords);
    }

    [Fact] // test 7: an oversized glossary is still rejected when a context is built
    public void OversizedGlossary_StillRejected()
    {
        var many = Enumerable.Range(0, Hotwords.MaxCount + 5).Select(i => "t" + i).ToArray();
        Assert.Throws<ArgumentException>(() => Hotwords.Normalize(many));
    }
}
