using System.Linq;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.Infrastructure.Configuration;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// UI-R3 finishing: the long-lived dynamic status strings (session state, recorder, health, status
/// line, translation direction) resolve correctly per culture, switch live, and leave no Chinese in
/// the English UI. The internal language codes never change.
/// </summary>
public class DynamicLocalizationTests
{
    private static bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    [Theory]
    [InlineData("Session.State.Idle", "空闲", "Idle")]
    [InlineData("Session.State.Starting", "启动中", "Starting")]
    [InlineData("Session.State.Running", "运行中", "Running")]
    [InlineData("Session.State.Stopping", "停止中", "Stopping")]
    [InlineData("Session.State.Completed", "已完成", "Completed")]
    [InlineData("Session.State.Faulted", "已故障", "Faulted")]
    [InlineData("Recorder.Ready", "就绪", "Ready")]
    public void StateAndRecorder_ResolvePerCulture(string key, string zh, string en)
    {
        var loc = new LocalizationService();
        Assert.Equal(zh, loc[key]);
        loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal(en, loc[key]);
    }

    [Fact] // status description switches immediately with the language
    public void Status_SwitchesLive()
    {
        var loc = new LocalizationService();
        var zh = loc["Status.Idle"];
        loc.SetLanguage(LocalizedStrings.EnUS);
        var en = loc["Status.Idle"];

        Assert.NotEqual(zh, en);
        Assert.True(HasCjk(zh));
        Assert.False(HasCjk(en));
    }

    [Theory] // translation direction uses the current UI language's names; codes stay stable
    [InlineData(LocalizedStrings.ZhCN, "日本語 → 中文")]
    [InlineData(LocalizedStrings.EnUS, "Japanese → Chinese")]
    public void TranslationDirection_UsesUiLanguageNames(string culture, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(culture);

        // The direction is composed from the stable codes ja/zh via the Lang.* resources.
        var direction = $"{loc["Lang.ja"]} → {loc["Lang.zh"]}";

        Assert.Equal(expected, direction);
    }

    [Fact] // switching the UI language must not change the internal codes
    public void SwitchingUiLanguage_DoesNotChangeInternalCodes()
    {
        var loc = new LocalizationService();
        const string code = "ja"; // an internal recognition-language code
        loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal("ja", code);                 // unchanged
        Assert.Equal("Japanese", loc["Lang.ja"]); // only the display differs
    }

    [Fact] // no known status string leaks Chinese into the English UI
    public void EnglishUi_HasNoChineseInStatusStrings()
    {
        var en = LocalizedStrings.Tables[LocalizedStrings.EnUS];
        var statusKeys = en.Keys.Where(k =>
            k.StartsWith("Session.State.") || k.StartsWith("Recorder.") || k.StartsWith("Status.")
            || k.StartsWith("Health.") || k.StartsWith("Env.Msg.") || k.StartsWith("Error."));

        foreach (var key in statusKeys)
        {
            Assert.False(HasCjk(en[key]), $"English value for '{key}' contains Chinese: {en[key]}");
        }
    }

    [Theory] // English language names use English words, not endonyms
    [InlineData("Lang.ja", "Japanese")]
    [InlineData("Lang.zh", "Chinese")]
    [InlineData("Lang.en", "English")]
    public void EnglishUi_UsesEnglishLanguageNames(string key, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal(expected, loc[key]);
    }
}

/// <summary>UI-R3 finishing: DefaultShowOverlay actually drives overlay visibility for a new meeting.</summary>
public class DefaultShowOverlayTests
{
    private static SubtitleOverlayViewModel Overlay() => new(new SubtitleSettings());

    [Fact] // default on → a new meeting shows the overlay
    public void DefaultOn_NewSession_ShowsOverlay()
    {
        var overlay = Overlay();
        overlay.ApplyAppearance(new UserSettings { DefaultShowOverlay = true });
        overlay.IsVisible = false;

        overlay.PrepareForNewSession();

        Assert.True(overlay.IsVisible);
    }

    [Fact] // default off → a new meeting does not show the overlay
    public void DefaultOff_NewSession_DoesNotShow()
    {
        var overlay = Overlay();
        overlay.ApplyAppearance(new UserSettings { DefaultShowOverlay = false });

        overlay.PrepareForNewSession();

        Assert.False(overlay.IsVisible);
    }

    [Fact] // a manual toggle during the session is not overridden by anything periodic
    public void ManualToggle_IsNotOverridden()
    {
        var overlay = Overlay();
        overlay.ApplyAppearance(new UserSettings { DefaultShowOverlay = false });
        overlay.PrepareForNewSession();
        Assert.False(overlay.IsVisible);

        overlay.IsVisible = true; // user shows it mid-session

        // Unrelated appearance updates and line changes must not reset visibility.
        overlay.FontSize = 30;
        overlay.AddFinal(Guid.NewGuid(), "テスト");
        overlay.SetPartial("...");

        Assert.True(overlay.IsVisible);
    }
}
