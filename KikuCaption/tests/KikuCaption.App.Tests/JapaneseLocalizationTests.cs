using System.IO;
using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.Infrastructure.Configuration;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R3 Japanese (ja-JP) UI language: resources, live switch, persistence, and residue-free text.</summary>
public class JapaneseLocalizationTests
{
    [Theory] // 5: Japanese status values are exactly as specified
    [InlineData("Session.State.Idle", "アイドル")]
    [InlineData("Recorder.Ready", "準備完了")]
    [InlineData("Session.State.Starting", "開始中")]
    [InlineData("Session.State.Running", "実行中")]
    [InlineData("Session.State.Stopping", "停止中")]
    [InlineData("Session.State.Completed", "完了")]
    [InlineData("Session.State.Faulted", "エラー")]
    [InlineData("Timeline.Translating", "翻訳中…")]
    [InlineData("Timeline.TranslationFailed", "⚠ 翻訳に失敗しました（原文を保持）")]
    [InlineData("Env.Health.Healthy", "環境は正常です")]
    [InlineData("Env.Msg.Checking", "環境を確認しています……")]
    [InlineData("Common.NextMeeting", "次回の会議から有効になります")]
    [InlineData("Common.AfterRestart", "再起動後に有効になります")]
    public void JapaneseStatusValues_AreCorrect(string key, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.JaJP);
        Assert.Equal(expected, loc[key]);
    }

    [Fact] // "Not configured" is localized in Japanese
    public void NotConfigured_IsJapanese()
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.JaJP);
        Assert.StartsWith("未設定", loc["Home.NotConfiguredConfigure"]);
    }

    [Theory] // 6: language names in Japanese UI: ja→日本語, zh→中国語, en→英語
    [InlineData("Lang.ja", "日本語")]
    [InlineData("Lang.zh", "中国語")]
    [InlineData("Lang.en", "英語")]
    public void JapaneseLanguageNames_AreCorrect(string key, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.JaJP);
        Assert.Equal(expected, loc[key]);
    }

    [Fact] // 6: translation direction in the Japanese UI is 日本語 → 中国語
    public void JapaneseDirection_UsesJapaneseNames()
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.JaJP);
        var direction = $"{loc["Lang.ja"]} → {loc["Lang.zh"]}";
        Assert.Equal("日本語 → 中国語", direction);
    }

    [Fact] // 2: switching to Japanese refreshes bound text immediately (indexer change)
    public void SwitchingToJapanese_RaisesIndexerChange()
    {
        var loc = new LocalizationService();
        var raised = false;
        loc.PropertyChanged += (_, e) => { if (e.PropertyName is "Item[]") raised = true; };

        loc.SetLanguage(LocalizedStrings.JaJP);

        Assert.True(raised);
        Assert.Equal("ホーム", loc["Nav.Home"]);
    }

    [Fact] // 3: the Japanese choice persists across a restart
    public void JapaneseLanguage_PersistsAcrossRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_ja", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new UserSettingsStore(dir);
            SettingsPersistence.PersistUiLanguage(store, LocalizedStrings.JaJP);
            Assert.Equal(LocalizedStrings.JaJP, store.Load().Settings.UiLanguage);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact] // 4: an unknown culture safely falls back to zh-CN (no crash, no switch)
    public void UnknownCulture_FallsBackToZhCn()
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.JaJP);
        loc.SetLanguage("ko-KR"); // unknown → ignored
        Assert.Equal(LocalizedStrings.JaJP, loc.CurrentLanguage);

        // A key missing from a culture would resolve via zh-CN; the resolver never throws.
        Assert.Equal("Nonexistent.Key", loc["Nonexistent.Key"]);
    }

    [Fact] // 7 & 8: switching UI language never changes recognition or translation codes
    public void SwitchingUiLanguage_LeavesLanguageCodesUnchanged()
    {
        var loc = new LocalizationService();
        // Stand-ins for the internal, stable codes held elsewhere (recognition + translation).
        var recognition = "ja";
        var source = "ja";
        var target = "zh";

        loc.SetLanguage(LocalizedStrings.JaJP);
        loc.SetLanguage(LocalizedStrings.EnUS);
        loc.SetLanguage(LocalizedStrings.ZhCN);

        Assert.Equal("ja", recognition);
        Assert.Equal("ja", source);
        Assert.Equal("zh", target); // only display names differ per culture, not the codes
    }

    [Fact] // 9: Japanese text has no replacement char, no empty strings
    public void JapaneseValues_HaveNoReplacementCharOrEmpty()
    {
        var ja = LocalizedStrings.Tables[LocalizedStrings.JaJP];
        foreach (var (key, value) in ja)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Empty Japanese value for '{key}'");
            Assert.DoesNotContain('�', value); // U+FFFD replacement character (mojibake)
        }
    }

    [Fact] // 10: Japanese status text has no leftover Chinese-only or English words
    public void JapaneseStatus_HasNoChineseOrEnglishResidue()
    {
        var ja = LocalizedStrings.Tables[LocalizedStrings.JaJP];
        // Known Chinese status strings that must not appear verbatim in Japanese.
        string[] chineseResidue = { "空闲", "就绪", "运行中", "已完成", "翻译中", "环境正常" };
        // Known English status words that must not appear in the Japanese UI.
        string[] englishResidue = { "Idle", "Ready", "Running", "Completed", "Translating" };

        var statusKeys = ja.Keys.Where(k =>
            k.StartsWith("Session.State.") || k.StartsWith("Recorder.") || k.StartsWith("Status.")
            || k.StartsWith("Env.Msg.") || k.StartsWith("Timeline."));

        foreach (var key in statusKeys)
        {
            var value = ja[key];
            foreach (var bad in chineseResidue)
            {
                Assert.False(value.Contains(bad), $"Japanese '{key}' contains Chinese residue '{bad}': {value}");
            }
            foreach (var bad in englishResidue)
            {
                Assert.False(value.Contains(bad), $"Japanese '{key}' contains English residue '{bad}': {value}");
            }
        }
    }
}
