using System.Globalization;
using System.IO;
using KikuCaption.App.Converters;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R3 subtitle settings live preview + persistence, and the language-display converter.</summary>
public class SubtitleSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly UserSettingsStore _store;

    public SubtitleSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kiku_r3_subs", Guid.NewGuid().ToString("N"));
        _store = new UserSettingsStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private SubtitleSettingsViewModel NewVm(out SubtitleOverlayViewModel overlay)
    {
        overlay = new SubtitleOverlayViewModel(new SubtitleSettings());
        return new SubtitleSettingsViewModel(overlay, _store, new LocalizationService(), NullLogger<SubtitleSettingsViewModel>.Instance);
    }

    [Fact] // editing appearance updates the same overlay the preview binds to (live preview)
    public void EditingAppearance_UpdatesLiveOverlay()
    {
        var vm = NewVm(out var overlay);

        vm.Overlay.FontSize = 40;
        vm.Overlay.OriginalColor = "#112233";

        // The settings VM exposes the very same live overlay instance (preview + real overlay).
        Assert.Same(overlay, vm.Overlay);
        Assert.Equal(40, overlay.FontSize);
        Assert.Equal("#112233", overlay.OriginalColor);
    }

    [Fact] // save persists appearance across a restart
    public void Save_PersistsAppearance()
    {
        var vm = NewVm(out var overlay);
        overlay.FontSize = 40;
        overlay.ShowTranslation = false;
        overlay.TranslationColor = "#00FF00";
        overlay.DefaultVisible = true;

        vm.SaveCommand.Execute(null);

        var (s, _) = _store.Load();
        Assert.Equal(40, s.SubtitleFontSize);
        Assert.False(s.SubtitleShowTranslation);
        Assert.Equal("#00FF00", s.SubtitleTranslationColor);
        Assert.True(s.DefaultShowOverlay);
    }

    [Fact] // restore defaults resets the live overlay appearance
    public void RestoreDefaults_ResetsAppearance()
    {
        var vm = NewVm(out var overlay);
        overlay.FontSize = 40;

        vm.RestoreDefaultsCommand.Execute(null);

        Assert.Equal(new UserSettings().SubtitleFontSize, overlay.FontSize);
    }

    [Theory]
    [InlineData("ja", "日本語")]
    [InlineData("zh", "中文")]
    [InlineData("en", "English")]
    public void LanguageDisplayConverter_ShowsEndonym(string code, string expected)
    {
        var converter = new LanguageDisplayConverter();
        var result = converter.Convert(code, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }
}
