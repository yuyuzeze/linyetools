using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Subtitle appearance settings with a live preview (UI-R3). Appearance is edited on the live
/// <see cref="SubtitleOverlayViewModel"/>, so every change reflects immediately in both the preview
/// and the real overlay (safe display settings apply live). Disk persistence is explicit (Save) so
/// dragging a slider never writes to disk. The API key is never touched here.
/// </summary>
public sealed partial class SubtitleSettingsViewModel : ObservableObject
{
    private readonly SubtitleOverlayViewModel _overlay;
    private readonly UserSettingsStore _store;
    private readonly LocalizationService _localization;
    private readonly ILogger<SubtitleSettingsViewModel> _logger;

    public SubtitleSettingsViewModel(
        SubtitleOverlayViewModel overlay,
        UserSettingsStore store,
        LocalizationService localization,
        ILogger<SubtitleSettingsViewModel> logger)
    {
        _overlay = overlay;
        _store = store;
        _localization = localization;
        _logger = logger;
    }

    /// <summary>The live overlay appearance — bound two-way so edits apply to preview + overlay at once.</summary>
    public SubtitleOverlayViewModel Overlay => _overlay;

    [ObservableProperty] private string _statusText = string.Empty;

    public string SampleOriginal => _localization["Subtitle.SampleOriginal"];
    public string SampleTranslation => _localization["Subtitle.SampleTranslation"];
    public string SamplePartial => _localization["Subtitle.SamplePartial"];

    public IReadOnlyList<string> FontFamilies { get; } = new[]
    {
        "Segoe UI, Microsoft YaHei UI", "Microsoft YaHei UI", "Yu Gothic UI", "Meiryo", "Consolas"
    };

    [RelayCommand]
    private void Save()
    {
        try
        {
            var (existing, _) = _store.Load();
            _store.Save(existing with
            {
                DefaultShowOverlay = _overlay.DefaultVisible,
                SubtitleFontSize = _overlay.FontSize,
                SubtitleFontFamily = _overlay.FontFamily,
                SubtitleOpacity = _overlay.BackgroundOpacity,
                SubtitleMaxLines = _overlay.MaxLines,
                SubtitleTopmost = _overlay.Topmost,
                ClickThrough = _overlay.ClickThrough,
                SubtitleShowOriginal = _overlay.ShowOriginal,
                SubtitleShowTranslation = _overlay.ShowTranslation,
                SubtitleOriginalColor = _overlay.OriginalColor,
                SubtitleTranslationColor = _overlay.TranslationColor,
                SubtitlePartialOpacity = _overlay.PartialOpacity
            });
            StatusText = _localization["Settings.Saved"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving subtitle settings failed.");
            StatusText = _localization["Settings.SaveFailed"];
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        // Apply the record defaults live to the overlay (preview updates immediately).
        _overlay.ApplyAppearance(new UserSettings());
    }
}
