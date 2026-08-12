using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Core.Tests;

/// <summary>
/// UI-R4B: when wired with an <see cref="ISpeechDictionaryStore"/>, the provider snapshots the
/// language's ACTIVE dictionary at ForLanguage time, keeps ja/zh isolated, leaves base options
/// untouched, and returns copies (so later edits can't mutate a running session's snapshot).
/// </summary>
public class SpeechOptionsProviderStoreTests
{
    private sealed class FakeStore : ISpeechDictionaryStore
    {
        private readonly Dictionary<string, SpeechDictionaryProfile> _active;
        public FakeStore(Dictionary<string, SpeechDictionaryProfile> active) => _active = active;

        public SpeechDictionaryProfile GetActiveProfile(string languageCode) => _active[languageCode];

        // Unused by the provider.
        public IReadOnlyList<SpeechDictionaryProfile> GetProfiles(string? languageCode = null) => _active.Values.ToList();
        public SpeechDictionaryProfile? GetById(Guid id) => _active.Values.FirstOrDefault(p => p.Id == id);
        public Guid GetActiveId(string languageCode) => _active[languageCode].Id;
        public SpeechDictionaryProfile Upsert(SpeechDictionaryProfile profile) => profile;
        public void SetActive(string languageCode, Guid id) { }
        public void Delete(Guid id) { }
        public void RestoreBuiltInDefaults() { }
    }

    private static SpeechDictionaryProfile Profile(string lang, string prompt, params string[] hotwords) => new()
    {
        Id = Guid.NewGuid(),
        Name = lang,
        LanguageCode = lang,
        InitialPrompt = prompt,
        Hotwords = hotwords
    };

    private static SpeechOptionsProvider Provider() => new(
        new SpeechOptions { Model = "small", Device = "cpu", ComputeType = "int8", BeamSize = 3, Language = "ja", ModelCacheDirectory = @"C:\m" },
        new FakeStore(new Dictionary<string, SpeechDictionaryProfile>
        {
            ["ja"] = Profile("ja", "日本語の会議", "Azure", "ファイル検索"),
            ["zh"] = Profile("zh", "中文会议", "Azure", "OpenAI"),
        }));

    [Fact] // ja reads the ja active dictionary
    public void Ja_UsesActiveJapaneseDictionary()
    {
        var o = Provider().ForLanguage("ja");
        Assert.Equal("ja", o.Language);
        Assert.Equal("日本語の会議", o.InitialPrompt);
        Assert.Equal(new[] { "Azure", "ファイル検索" }, o.Hotwords);
    }

    [Fact] // zh never receives the ja context and vice versa
    public void LanguagesAreIsolated()
    {
        var zh = Provider().ForLanguage("zh");
        Assert.Equal("中文会议", zh.InitialPrompt);
        Assert.DoesNotContain("ファイル検索", zh.Hotwords!);
    }

    [Fact] // base model/compute/beam/cache are unaffected by the dictionary
    public void BaseOptions_Unaffected()
    {
        var o = Provider().ForLanguage("ja");
        Assert.Equal("small", o.Model);
        Assert.Equal("int8", o.ComputeType);
        Assert.Equal(3, o.BeamSize);
        Assert.Equal(@"C:\m", o.ModelCacheDirectory);
    }

    [Fact] // an unsupported recognition language gets no context (base only)
    public void UnsupportedLanguage_GetsNoContext()
    {
        var o = Provider().ForLanguage("en");
        Assert.Equal("en", o.Language);
        Assert.Null(o.InitialPrompt);
        Assert.Null(o.Hotwords);
    }

    [Fact] // the returned hotwords are a copy — mutating the store's list can't change the snapshot
    public void ReturnedHotwords_AreACopy()
    {
        var active = Profile("ja", "p", "A", "B");
        var provider = new SpeechOptionsProvider(
            new SpeechOptions { Language = "ja" },
            new FakeStore(new Dictionary<string, SpeechDictionaryProfile> { ["ja"] = active }));

        var snapshot = provider.ForLanguage("ja");
        Assert.NotSame(active.Hotwords, snapshot.Hotwords);
        Assert.Equal(new[] { "A", "B" }, snapshot.Hotwords);
    }
}
