using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>UI language option: stable culture code + a fixed display label.</summary>
public sealed record UiLanguageOption(string Code, string Display);

/// <summary>
/// General settings (UI-R3). Edits are persisted explicitly (Save) — no per-keystroke disk writes —
/// except the UI language, which also applies live for immediate feedback and is persisted on
/// change (a discrete choice, so restart-persistence needs no Save). The API key is never stored
/// here. Saving is atomic with an explicit failure notice.
/// </summary>
public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly UserSettingsStore _store;
    private readonly LocalizationService _localization;
    private readonly ILogger<GeneralSettingsViewModel> _logger;
    private bool _loading;

    public GeneralSettingsViewModel(UserSettingsStore store, LocalizationService localization, ILogger<GeneralSettingsViewModel> logger)
    {
        _store = store;
        _localization = localization;
        _logger = logger;
        LoadFromStore();
    }

    public IReadOnlyList<UiLanguageOption> UiLanguages { get; } = new[]
    {
        new UiLanguageOption(LocalizedStrings.ZhCN, "简体中文"),
        new UiLanguageOption(LocalizedStrings.EnUS, "English"),
        new UiLanguageOption(LocalizedStrings.JaJP, "日本語")
    };

    public IReadOnlyList<string> RecognitionLanguages { get; } = new[] { "ja", "zh" };
    public IReadOnlyList<string> RecordingTargets { get; } = new[] { "screen", "window" };

    [ObservableProperty] private string _uiLanguage = LocalizedStrings.ZhCN;
    [ObservableProperty] private string _defaultRecognitionLanguage = "ja";
    [ObservableProperty] private string? _outputDirectory;
    [ObservableProperty] private bool _loadRecentOnStartup;
    [ObservableProperty] private bool _defaultTranslationEnabled;
    [ObservableProperty] private string _defaultRecordingTarget = "screen";
    [ObservableProperty] private int _logRetentionDays = 14;
    [ObservableProperty] private string _statusText = string.Empty;

    // UI language applies live and persists immediately (discrete choice, safe to write once).
    partial void OnUiLanguageChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _localization.SetLanguage(value);
        try { SettingsPersistence.PersistUiLanguage(_store, value); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persisting UI language failed."); }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var (existing, _) = _store.Load();
            _store.Save(existing with
            {
                UiLanguage = UiLanguage,
                RecognitionLanguage = DefaultRecognitionLanguage,
                OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory,
                LoadRecentOnStartup = LoadRecentOnStartup,
                TranslationEnabled = DefaultTranslationEnabled, // shares the existing translation on/off flag
                CaptureType = DefaultRecordingTarget,
                LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365)
            });
            StatusText = _localization["Settings.Saved"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving general settings failed.");
            StatusText = _localization["Settings.SaveFailed"];
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var d = new UserSettings();
        _loading = true;
        DefaultRecognitionLanguage = d.RecognitionLanguage;
        OutputDirectory = d.OutputDirectory;
        LoadRecentOnStartup = d.LoadRecentOnStartup;
        DefaultTranslationEnabled = d.TranslationEnabled;
        DefaultRecordingTarget = d.CaptureType;
        LogRetentionDays = d.LogRetentionDays;
        _loading = false;
        // Language reset applies live + persists via the changed handler.
        UiLanguage = d.UiLanguage;
    }

    /// <summary>Sets the output directory (called from the view's folder-browse dialog).</summary>
    public void SetOutputDirectory(string path) => OutputDirectory = path;

    private void LoadFromStore()
    {
        var (s, _) = _store.Load();
        _loading = true;
        UiLanguage = LocalizationService.NormalizeCulture(s.UiLanguage);
        DefaultRecognitionLanguage = s.RecognitionLanguage;
        OutputDirectory = s.OutputDirectory;
        LoadRecentOnStartup = s.LoadRecentOnStartup;
        DefaultTranslationEnabled = s.TranslationEnabled;
        DefaultRecordingTarget = s.CaptureType;
        LogRetentionDays = s.LogRetentionDays;
        _loading = false;
    }
}
