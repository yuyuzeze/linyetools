using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Progress;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// One model row on the environment page (R7B): status, size, phase, progress, error, and the
/// download / cancel / retry actions. When speech recognition is off the row is a neutral,
/// non-actionable "not needed" state — never a red blocker (UI-R6A). Errors are shown by localized
/// code; no URL/credential/caption text is ever surfaced or logged.
/// </summary>
public sealed partial class ModelDownloadViewModel : ObservableObject
{
    private readonly ModelCatalogEntry _entry;
    private readonly IModelDownloadCoordinator _coordinator;
    private readonly LocalizationService _loc;
    private readonly SessionModeState _mode;
    private readonly ILogger<ModelDownloadViewModel> _logger;
    private CancellationTokenSource? _cts;

    public ModelDownloadViewModel(
        ModelCatalogEntry entry,
        IModelDownloadCoordinator coordinator,
        LocalizationService loc,
        SessionModeState mode,
        ILogger<ModelDownloadViewModel> logger)
    {
        _entry = entry;
        _coordinator = coordinator;
        _loc = loc;
        _mode = mode;
        _logger = logger;
        _mode.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(SpeechEnabled)); RaiseState(); };
        RefreshStatus();
    }

    [ObservableProperty] private bool _installed;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private KikuCaption.Core.Models.ModelVerificationLevel _verificationLevel;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _phaseKey = string.Empty;
    [ObservableProperty] private double _progress;      // 0..1
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string? _errorCode;

    public string ComponentId => _entry.ComponentId;
    public bool SpeechEnabled => _mode.SpeechRecognitionEnabled;

    /// <summary>R7B.1: a real download source must be configured before Download is offered.</summary>
    public bool RemoteConfigured => _coordinator.RemoteConfigured;

    private bool RuntimeVerified => VerificationLevel == KikuCaption.Core.Models.ModelVerificationLevel.RuntimeVerified;

    public string Name => _loc[_entry.NameKey];
    public string RoleText => _loc[_entry.Role == ModelRole.Realtime ? "Models.Role.Realtime" : "Models.Role.Correction"];

    /// <summary>Localized status. Installed but not yet runtime-verified is shown distinctly (not "Ready").</summary>
    public string StatusText
    {
        get
        {
            if (Installed)
            {
                return RuntimeVerified ? _loc["Models.Installed"] : _loc["Models.DownloadedPendingRuntime"];
            }

            return SpeechEnabled ? _loc["Models.Missing"] : _loc["Models.NotNeeded"];
        }
    }

    /// <summary>Green = runtime-verified; blue = downloaded/structure-only; grey = not needed; amber = missing.</summary>
    public string StatusColor
    {
        get
        {
            if (Installed)
            {
                return RuntimeVerified ? "#2E7D32" : "#1565C0";
            }

            return SpeechEnabled ? "#F9A825" : "#616161";
        }
    }

    public bool HasSize => SizeBytes > 0;
    public string SizeText => FormatSize(SizeBytes);

    public string PhaseText => string.IsNullOrEmpty(PhaseKey) ? string.Empty : _loc[PhaseKey];

    public bool HasError => !string.IsNullOrEmpty(ErrorCode);
    // The code is a stable, non-sensitive slug (e.g. "sha256", "no-manifest"); shown as a diagnostic
    // suffix on a localized message. No URL/credential/caption text is ever included.
    public string ErrorText => HasError ? string.Format(_loc["Models.Error"], ErrorCode) : string.Empty;

    /// <summary>Shown (instead of a Download button) when no manifest source is configured.</summary>
    public bool ShowNoSource => !RemoteConfigured && !Installed;
    public string NoSourceText => _loc["Models.NoSource"];

    // Download is NOT gated by SpeechRecognitionEnabled (users may pre-install while recording-only);
    // it only requires a configured source and that the model is not already installed.
    public bool CanDownload => RemoteConfigured && !Installed && !IsDownloading;
    public bool CanCancel => IsDownloading;
    public bool CanRetry => RemoteConfigured && !IsDownloading && HasError;

    /// <summary>Tooltip: role, size and a CPU-time caution (medium correction can be slow).</summary>
    public string Tooltip => _entry.Role == ModelRole.Correction
        ? _loc["Models.Tooltip.Medium"]
        : _loc["Models.Tooltip.Small"];

    public void RefreshStatus()
    {
        var status = _coordinator.GetStatus(_entry);
        Installed = status.Installed;
        SizeBytes = status.SizeBytes;
        VerificationLevel = status.VerificationLevel;
        OnPropertyChanged(nameof(HasSize));
        OnPropertyChanged(nameof(SizeText));
        RaiseState();
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(NoSourceText));
    }

    [RelayCommand]
    private Task DownloadAsync() => RunInstallAsync();

    [RelayCommand]
    private Task RetryAsync() => RunInstallAsync();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunInstallAsync()
    {
        if (IsDownloading)
        {
            return;
        }

        ErrorCode = null;
        IsDownloading = true;
        IsIndeterminate = true;
        Progress = 0;
        RaiseState();

        _cts = new CancellationTokenSource();
        var progress = new Progress<ComponentInstallProgress>(OnProgress);
        try
        {
            await _coordinator.InstallAsync(_entry, progress, _cts.Token);
            PhaseKey = ComponentInstallPhases.Completed;
        }
        catch (OperationCanceledException)
        {
            PhaseKey = string.Empty; // cancelled: silent, no error banner
        }
        catch (ComponentInstallException ex)
        {
            _logger.LogWarning("Model install failed for {Id}: {Code}", _entry.ComponentId, ex.Code);
            ErrorCode = ex.Code;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model install failed for {Id}.", _entry.ComponentId);
            ErrorCode = "unexpected";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsDownloading = false;
            IsIndeterminate = false;
            RefreshStatus(); // re-check presence; only this model's status changes
            OnPropertyChanged(nameof(PhaseText));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorText));
        }
    }

    private void OnProgress(ComponentInstallProgress value)
    {
        PhaseKey = value.PhaseKey;
        if (value.Fraction is { } fraction)
        {
            IsIndeterminate = false;
            Progress = fraction;
        }
        else
        {
            IsIndeterminate = true;
        }

        OnPropertyChanged(nameof(PhaseText));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(ShowNoSource));
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    partial void OnInstalledChanged(bool value) => RaiseState();
    partial void OnVerificationLevelChanged(KikuCaption.Core.Models.ModelVerificationLevel value) => RaiseState();
    partial void OnIsDownloadingChanged(bool value) => RaiseState();
    partial void OnErrorCodeChanged(string? value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ErrorText)); RaiseState(); }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0} MB";
    }
}
