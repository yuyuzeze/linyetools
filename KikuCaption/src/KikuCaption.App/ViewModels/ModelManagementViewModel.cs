using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// The "speech recognition models" section on the environment page (R7B): separate small (real-time)
/// and medium (correction) rows, each independently downloadable. When speech recognition is off the
/// whole section is a neutral, non-blocking informational area (UI-R6A).
/// </summary>
public sealed partial class ModelManagementViewModel : ObservableObject
{
    private readonly SessionModeState _mode;
    private readonly LocalizationService _loc;

    public ModelManagementViewModel(
        ModelCatalog catalog,
        IModelDownloadCoordinator coordinator,
        LocalizationService loc,
        SessionModeState mode,
        ILoggerFactory loggerFactory)
    {
        _mode = mode;
        _loc = loc;

        foreach (var entry in catalog.Entries)
        {
            Models.Add(new ModelDownloadViewModel(entry, coordinator, loc, mode,
                loggerFactory.CreateLogger<ModelDownloadViewModel>()));
        }

        _mode.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SpeechEnabled));
        _loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            foreach (var model in Models)
            {
                model.RefreshLocalizedText();
            }
        };
    }

    public ObservableCollection<ModelDownloadViewModel> Models { get; } = new();

    public bool SpeechEnabled => _mode.SpeechRecognitionEnabled;

    public string Title => _loc["Models.Title"];

    /// <summary>Re-checks each model's on-disk presence (called after an install or a re-check).</summary>
    public void RefreshStatus()
    {
        foreach (var model in Models)
        {
            model.RefreshStatus();
        }
    }
}
