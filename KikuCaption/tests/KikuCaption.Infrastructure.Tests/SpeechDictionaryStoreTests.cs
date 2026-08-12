using System.Text.Json;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

/// <summary>UI-R4B: file-backed dictionary store — seeding, migration, atomicity, recovery, CRUD.</summary>
public class SpeechDictionaryStoreTests : IDisposable
{
    private readonly string _dir;

    public SpeechDictionaryStoreTests()
        => _dir = Path.Combine(Path.GetTempPath(), "kiku_dict", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static IReadOnlyDictionary<string, SpeechContext> Seeds() => new Dictionary<string, SpeechContext>
    {
        ["ja"] = new("技術会議の字幕。", new[] { "Azure", "OpenAI" }),
        ["zh"] = new("这是技术会议。", new[] { "Azure" }),
    };

    private SpeechDictionaryStore NewStore(ILogger<SpeechDictionaryStore>? logger = null)
        => new(_dir, Seeds(), logger ?? NullLogger<SpeechDictionaryStore>.Instance);

    private string FilePath => Path.Combine(_dir, "dictionaries.json");

    [Fact] // first launch seeds exactly two built-ins from appsettings, both active
    public void FirstLaunch_SeedsTwoBuiltInsFromAppsettings()
    {
        var store = NewStore();

        var all = store.GetProfiles();
        Assert.Equal(2, all.Count);
        Assert.All(all, p => Assert.True(p.IsBuiltIn));

        var ja = store.GetById(SpeechDictionaryProfile.BuiltInJapaneseId)!;
        Assert.Equal("ja", ja.LanguageCode);
        Assert.Equal("技術会議の字幕。", ja.InitialPrompt);            // migrated from appsettings
        Assert.Equal(new[] { "Azure", "OpenAI" }, ja.Hotwords);
        Assert.Equal(SpeechDictionaryProfile.BuiltInJapaneseId, store.GetActiveId("ja"));
        Assert.Equal(SpeechDictionaryProfile.BuiltInChineseId, store.GetActiveId("zh"));
    }

    [Fact] // restarting does not duplicate the built-ins (idempotent seeding by stable id)
    public void Restart_DoesNotDuplicateBuiltIns()
    {
        NewStore();
        var reopened = NewStore();
        Assert.Equal(2, reopened.GetProfiles().Count);
    }

    [Fact] // a user dictionary and the active selection survive a restart
    public void UserDictionaryAndActive_Persist()
    {
        var store = NewStore();
        var saved = store.Upsert(new SpeechDictionaryProfile
        {
            Id = Guid.NewGuid(), Name = "融资业务", LanguageCode = "ja", Hotwords = new[] { "IPO", "M&A" }
        });
        store.SetActive("ja", saved.Id);

        var reopened = NewStore();
        Assert.Contains(reopened.GetProfiles("ja"), p => p.Id == saved.Id && p.Name == "融资业务");
        Assert.Equal(saved.Id, reopened.GetActiveId("ja"));
    }

    [Fact] // saving is atomic and leaves no temp file behind
    public void Save_IsAtomic_NoTempLeftover()
    {
        var store = NewStore();
        store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "x", LanguageCode = "zh" });
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact] // a corrupt file is backed up and safe defaults are restored (no crash)
    public void CorruptFile_IsBackedUpAndReset()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not valid json ");

        var store = NewStore();

        Assert.Equal(2, store.GetProfiles().Count);                 // defaults restored
        Assert.Contains(Directory.GetFiles(_dir), f => f.Contains(".corrupt-")); // bad file preserved
    }

    [Fact] // built-in dictionaries can neither be edited nor deleted
    public void BuiltIns_AreReadOnlyAndUndeletable()
    {
        var store = NewStore();
        var ja = store.GetById(SpeechDictionaryProfile.BuiltInJapaneseId)!;

        Assert.Throws<InvalidOperationException>(() => store.Upsert(ja with { Name = "hacked" }));
        Assert.Throws<InvalidOperationException>(() => store.Delete(ja.Id));
    }

    [Fact] // deleting the ACTIVE user dictionary falls back to the built-in, atomically
    public void DeleteActive_FallsBackToBuiltIn()
    {
        var store = NewStore();
        var saved = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "temp", LanguageCode = "ja" });
        store.SetActive("ja", saved.Id);

        store.Delete(saved.Id);

        Assert.Equal(SpeechDictionaryProfile.BuiltInJapaneseId, store.GetActiveId("ja"));
        Assert.Null(store.GetById(saved.Id));
    }

    [Fact] // deleting a NON-active dictionary leaves the active selection untouched
    public void DeleteNonActive_KeepsActive()
    {
        var store = NewStore();
        var a = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "a", LanguageCode = "ja" });
        var b = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "b", LanguageCode = "ja" });
        store.SetActive("ja", a.Id);

        store.Delete(b.Id);

        Assert.Equal(a.Id, store.GetActiveId("ja"));
    }

    [Fact] // a duplicate name within one language is rejected; the same name across languages is allowed
    public void NameUniqueness_IsPerLanguage()
    {
        var store = NewStore();
        store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "会议", LanguageCode = "ja" });

        Assert.Throws<ArgumentException>(() =>
            store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = " 会议 ", LanguageCode = "ja" })); // case/trim-insensitive

        var zh = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "会议", LanguageCode = "zh" });
        Assert.Equal("会议", zh.Name); // same name, different language → allowed
    }

    [Fact] // SetActive rejects a profile whose language does not match
    public void SetActive_RejectsLanguageMismatch()
    {
        var store = NewStore();
        var zh = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "z", LanguageCode = "zh" });
        Assert.Throws<ArgumentException>(() => store.SetActive("ja", zh.Id));
    }

    [Fact] // GetActiveProfile is never null and falls back to the built-in when the active id is missing
    public void GetActiveProfile_FallsBackToBuiltIn()
    {
        var store = NewStore();
        var active = store.GetActiveProfile("ja");
        Assert.Equal(SpeechDictionaryProfile.BuiltInJapaneseId, active.Id);
    }

    [Fact] // the persisted file uses schemaVersion 1 + activeByLanguage + profiles, and holds no secret
    public void PersistedFile_HasExpectedShape_AndNoSecret()
    {
        var store = NewStore();
        store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "s", LanguageCode = "ja", Hotwords = new[] { "Azure" } });

        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("activeByLanguage", out _));
        Assert.True(root.TryGetProperty("profiles", out _));

        var raw = File.ReadAllText(FilePath);
        Assert.DoesNotContain("apikey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // logs never contain the prompt or hotword text (only id/language/count)
    public void Logs_DoNotLeakPromptOrHotwords()
    {
        var logger = new CapturingLogger();
        var store = new SpeechDictionaryStore(_dir, Seeds(), logger);
        store.Upsert(new SpeechDictionaryProfile
        {
            Id = Guid.NewGuid(), Name = "secret-name", LanguageCode = "ja",
            InitialPrompt = "TOP-SECRET-PROMPT", Hotwords = new[] { "SENSITIVE-TERM" }
        });

        var all = string.Join("\n", logger.Messages);
        Assert.DoesNotContain("TOP-SECRET-PROMPT", all);
        Assert.DoesNotContain("SENSITIVE-TERM", all);
        Assert.DoesNotContain("技術会議の字幕", all); // seed prompt not leaked either
    }

    [Fact] // concurrent readers and writers do not corrupt the store or throw
    public void ConcurrentReadWrite_IsSafe()
    {
        var store = NewStore();
        Parallel.For(0, 40, i =>
        {
            if (i % 2 == 0)
            {
                store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "p" + i, LanguageCode = "ja" });
            }
            else
            {
                _ = store.GetProfiles("ja");
                _ = store.GetActiveProfile("zh");
            }
        });

        // 2 built-ins + 20 user profiles, and the file is still valid JSON.
        Assert.Equal(22, store.GetProfiles().Count);
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
    }

    [Fact] // RestoreBuiltInDefaults re-seeds built-in content without touching user profiles
    public void RestoreBuiltInDefaults_ResetsSeedContent()
    {
        var store = NewStore();
        var user = store.Upsert(new SpeechDictionaryProfile { Id = Guid.NewGuid(), Name = "u", LanguageCode = "ja" });

        store.RestoreBuiltInDefaults();

        Assert.Equal("技術会議の字幕。", store.GetById(SpeechDictionaryProfile.BuiltInJapaneseId)!.InitialPrompt);
        Assert.NotNull(store.GetById(user.Id)); // user profile untouched
    }

    private sealed class CapturingLogger : ILogger<SpeechDictionaryStore>
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
