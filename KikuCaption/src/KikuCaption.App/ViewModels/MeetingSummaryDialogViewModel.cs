using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Summarization;
using KikuCaption.Translation;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// The "generate meeting summary" dialog (UI-R5C §4). Shows the (immutable) session info incl. the
/// validated total duration, lets the user pick the meeting format + output language + overwrite
/// behavior, runs generation with progress and cancellation, and never blocks the UI. All AI/file
/// work goes through <see cref="MeetingSummaryCoordinator"/>. The output language follows the persisted
/// user choice (or the UI language on first use), is snapshotted at generation start, and a later UI
/// language change never alters the running request.
/// </summary>
public sealed partial class MeetingSummaryDialogViewModel : ObservableObject
{
    private readonly SummarySessionContext _context;
    private readonly MeetingSummaryCoordinator _coordinator;
    private readonly LocalizationService _loc;
    private readonly UserSettingsStore _settings;
    private readonly string _model;
    private readonly ILogger _logger;
    private bool _loadingLanguage;
    private CancellationTokenSource? _cts;

    public MeetingSummaryDialogViewModel(
        SummarySessionContext context,
        MeetingSummaryCoordinator coordinator,
        LocalizationService loc,
        UserSettingsStore settings,
        TranslationOptions translation,
        MeetingSummaryOptions summaryOptions,
        ILogger logger)
    {
        _context = context;
        _coordinator = coordinator;
        _loc = loc;
        _settings = settings;
        _logger = logger;
        _model = string.IsNullOrWhiteSpace(summaryOptions.Model) ? translation.Model : summaryOptions.Model;
        _finalCount = context.FinalCount;
        SummaryExists = coordinator.SummaryExists(context.SessionDirectory);
        SaveAsVersion = true; // default to a timestamped version to avoid clobbering an existing summary

        // First use follows the UI language; a stored choice wins. The initial set must not persist.
        _loadingLanguage = true;
        var (stored, _) = settings.Load();
        OutputLanguage = SummaryLanguage.Resolve(stored.SummaryOutputLanguage, loc.CurrentLanguage);
        _loadingLanguage = false;
    }

    /// <summary>Reads the target session's final captions to show the exact count + total duration.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var (count, range) = await _coordinator.LoadStatsAsync(_context.SessionId, CancellationToken.None).ConfigureAwait(true);
            FinalCount = count;
            DurationText = range.HasValid ? FormatDuration(range.Duration) : _loc["Summary.Unknown"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading summary session stats failed.");
            DurationText = _loc["Summary.Unknown"];
        }
    }

    // ---- session info ---------------------------------------------------

    public string SessionDate => _context.SessionDate.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private int _finalCount;
    [ObservableProperty] private string _durationText = "…";
    public string SourceLanguageDisplay => _loc["Lang." + _context.SourceLanguage];
    public string SessionDirectory => _context.SessionDirectory;

    // ---- options --------------------------------------------------------

    /// <summary>False = single presenter (default); true = group discussion.</summary>
    [ObservableProperty] private bool _isGroup;

    /// <summary>Output language: "zh" | "ja" | "en".</summary>
    [ObservableProperty] private string _outputLanguage = "zh";

    /// <summary>When a summary already exists: true = timestamped version (default); false = overwrite.</summary>
    [ObservableProperty] private bool _saveAsVersion;

    [ObservableProperty] private bool _summaryExists;

    public IReadOnlyList<string> OutputLanguages { get; } = SummaryLanguage.Supported;

    // A genuine user choice is persisted and no longer follows the UI language (UI-R5C §setting).
    partial void OnOutputLanguageChanged(string value)
    {
        if (_loadingLanguage || !SummaryLanguage.Supported.Contains(value))
        {
            return;
        }

        try
        {
            var (existing, _) = _settings.Load();
            _settings.Save(existing with { SummaryOutputLanguage = value });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting summary output language failed.");
        }
    }

    // ---- run state ------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isGenerating;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string? _resultPath;

    public bool CanGenerate => !IsGenerating && MeetingSummaryCoordinator.CanGenerate(_context.State, FinalCount);

    public string PrivacyNote => _loc["Summary.PrivacyNote"];

    public bool IsBusy => IsGenerating;

    private MeetingType MeetingType => IsGroup ? Summarization.MeetingType.GroupDiscussion : Summarization.MeetingType.SinglePresenter;

    // Overwrite → the default file (atomic replace); version → a collision-safe timestamped name.
    private string FileName => SummaryExists && SaveAsVersion
        ? _coordinator.UniqueVersionedFileName(_context.SessionDirectory, DateTimeOffset.Now)
        : _coordinator.DefaultFileName;

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsGenerating || !CanGenerate)
        {
            return; // one generation per dialog; ignore repeat clicks
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            ErrorText = _loc["Summary.Error.NoApi"];
            return;
        }

        ErrorText = null;
        ResultPath = null;
        IsGenerating = true;
        _cts = new CancellationTokenSource();
        // Snapshot the output language NOW — a later UI/setting change cannot alter this request.
        var outputLanguage = OutputLanguage;
        var fileName = FileName;
        var progress = new Progress<MeetingSummaryProgress>(p => StatusText = PhaseText(p));

        try
        {
            var request = await _coordinator.BuildRequestAsync(
                _context with { FinalCount = FinalCount }, MeetingType, outputLanguage, _model, _cts.Token).ConfigureAwait(true);
            var result = await _coordinator.GenerateAsync(request, fileName, progress, _cts.Token).ConfigureAwait(true);
            ResultPath = result.OutputPath;
            SummaryExists = true;
            StatusText = _loc["Summary.Phase.Completed"];
        }
        catch (OperationCanceledException)
        {
            StatusText = _loc["Summary.Phase.Cancelled"]; // old summary is untouched (no write happened)
        }
        catch (MeetingSummaryException ex)
        {
            _logger.LogWarning("Summary failed: {Code}.", ex.Code); // code only — never captions/prompt/key
            ErrorText = _loc["Summary.Error.Failed"] + " (" + ex.Code
                + (string.IsNullOrWhiteSpace(ex.SafeDetail) ? "" : ": " + ex.SafeDetail) + ")";
            StatusText = _loc["Summary.Phase.Failed"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Summary failed (unexpected).");
            ErrorText = _loc["Summary.Error.Failed"];
            StatusText = _loc["Summary.Phase.Failed"];
        }
        finally
        {
            IsGenerating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenResult()
    {
        if (!_coordinator.OpenSummary(_context.SessionDirectory))
        {
            ErrorText = _loc["Summary.Error.OpenFailed"];
        }
    }

    [RelayCommand]
    private void ShowInFolder() => _coordinator.ShowInFolder(_context.SessionDirectory);

    private string PhaseText(MeetingSummaryProgress p) => p.Phase switch
    {
        MeetingSummaryPhase.Preparing => _loc["Summary.Phase.Preparing"],
        MeetingSummaryPhase.Mapping => string.Format(_loc["Summary.Phase.Mapping"], p.Current, p.Total),
        MeetingSummaryPhase.Reducing => _loc["Summary.Phase.Reducing"],
        MeetingSummaryPhase.Writing => _loc["Summary.Phase.Writing"],
        MeetingSummaryPhase.Completed => _loc["Summary.Phase.Completed"],
        MeetingSummaryPhase.Cancelled => _loc["Summary.Phase.Cancelled"],
        _ => _loc["Summary.Phase.Failed"]
    };

    private static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
}
