using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.App.Services.Python;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// R7C environment-page section for the managed Python speech-recognition environment: Install /
/// Repair / Cancel / Retry with phase + progress, versions on success, sanitized diagnostics, and a
/// grey non-blocking state when speech recognition is off. Raises <see cref="EnvironmentChanged"/>
/// after a successful install so the page re-checks probes + model runtime status.
/// </summary>
public sealed partial class PythonEnvironmentViewModel : ObservableObject
{
    private readonly IPythonEnvironmentInstaller _installer;
    private readonly LocalizationService _loc;
    private readonly SessionModeState _mode;
    private readonly ILogger<PythonEnvironmentViewModel> _logger;
    private CancellationTokenSource? _cts;

    public event EventHandler? EnvironmentChanged;

    public PythonEnvironmentViewModel(IPythonEnvironmentInstaller installer, LocalizationService loc,
        SessionModeState mode, ILogger<PythonEnvironmentViewModel> logger)
    {
        _installer = installer;
        _loc = loc;
        _mode = mode;
        _logger = logger;
        _mode.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(SpeechEnabled)); RaiseState(); };
        _loc.LanguageChanged += (_, _) => RefreshLocalizedText();
    }

    [ObservableProperty] private PythonEnvState _state = PythonEnvState.NoSystemPython;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private PythonInstallPhase _phase = PythonInstallPhase.Idle;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string? _errorCode;
    [ObservableProperty] private string? _pythonVersion;
    [ObservableProperty] private string? _fasterWhisperVersion;
    [ObservableProperty] private string? _cTranslate2Version;
    [ObservableProperty] private string _diagnostics = string.Empty;

    public bool SpeechEnabled => _mode.SpeechRecognitionEnabled;

    public string Title => _loc["Py.Title"];

    public string StatusText => State switch
    {
        PythonEnvState.VenvHealthy => _loc["Py.Ready"],
        PythonEnvState.VenvBroken => _loc["Py.Broken"],
        PythonEnvState.NoVenv => SpeechEnabled ? _loc["Py.NoVenv"] : _loc["Py.NotNeeded"],
        _ => SpeechEnabled ? _loc["Py.NoSystemPython"] : _loc["Py.NotNeeded"]
    };

    public string StatusColor
    {
        get
        {
            if (State == PythonEnvState.VenvHealthy) return "#2E7D32";  // green
            if (!SpeechEnabled) return "#616161";                        // grey, non-blocking
            return State == PythonEnvState.VenvBroken ? "#F9A825" : "#C62828"; // amber / red
        }
    }

    public bool HasVersions => State == PythonEnvState.VenvHealthy && FasterWhisperVersion is not null;
    public string VersionsText => HasVersions
        ? string.Format(_loc["Py.Versions"], PythonVersion, FasterWhisperVersion, CTranslate2Version)
        : string.Empty;

    public string PhaseText => Phase == PythonInstallPhase.Idle ? string.Empty : _loc["PyInstall." + Phase];
    public bool HasError => !string.IsNullOrEmpty(ErrorCode);
    public string ErrorText => !HasError
        ? string.Empty
        : ErrorCode == "locked"
            ? _loc["Py.Locked"]                       // R7C.1: another instance is installing
            : string.Format(_loc["Py.Error"], ErrorCode);

    // A compatible system Python must exist to install. Not gated by SpeechRecognitionEnabled — a user
    // may pre-install the environment while recording-only.
    public bool CanInstall => !IsBusy && State is PythonEnvState.NoVenv or PythonEnvState.VenvBroken && PythonVersionKnownForInstall;
    public bool CanRepair => !IsBusy && State == PythonEnvState.VenvBroken && PythonVersionKnownForInstall;
    public bool CanCancel => IsBusy;
    public bool CanRetry => !IsBusy && HasError;
    public bool ShowNoSystemPython => State == PythonEnvState.NoSystemPython && !IsBusy;

    // Whether a compatible system python was detected (fills SystemPythonVersion during Inspect).
    private bool PythonVersionKnownForInstall;

    public string InstallButtonText => State == PythonEnvState.VenvBroken ? _loc["Py.Repair"] : _loc["Py.Install"];

    public async Task RefreshAsync()
    {
        try
        {
            var status = await _installer.InspectAsync(CancellationToken.None);
            PythonVersionKnownForInstall = status.SystemPythonVersion is not null;
            State = status.State;
            PythonVersion = status.VenvPythonVersion ?? status.SystemPythonVersion;
            FasterWhisperVersion = status.FasterWhisperVersion;
            CTranslate2Version = status.CTranslate2Version;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python environment inspect failed.");
        }
        finally
        {
            RaiseAll();
        }
    }

    [RelayCommand]
    private Task InstallAsync() => RunAsync();

    [RelayCommand]
    private Task RetryAsync() => RunAsync();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenDownloadGuide()
    {
        try { Process.Start(new ProcessStartInfo("https://www.python.org/downloads/windows/") { UseShellExecute = true }); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not open the Python download page."); }
    }

    private async Task RunAsync()
    {
        if (IsBusy) return;
        ErrorCode = null;
        Diagnostics = string.Empty;
        IsBusy = true;
        IsIndeterminate = true;
        Phase = PythonInstallPhase.Inspecting;
        RaiseState();

        _cts = new CancellationTokenSource();
        var progress = new Progress<PythonInstallProgress>(OnProgress);
        try
        {
            var result = await _installer.InstallOrRepairAsync(progress, _cts.Token);
            if (result.Success)
            {
                Phase = PythonInstallPhase.Completed;
                EnvironmentChanged?.Invoke(this, EventArgs.Empty); // re-check probes + models
            }
            else if (result.FinalPhase == PythonInstallPhase.Cancelled)
            {
                Phase = PythonInstallPhase.Idle;
            }
            else
            {
                ErrorCode = result.ErrorCode;
                Phase = PythonInstallPhase.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            Phase = PythonInstallPhase.Idle;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Python environment install failed unexpectedly.");
            ErrorCode = "unexpected";
            Phase = PythonInstallPhase.Failed;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            IsIndeterminate = false;
            await RefreshAsync();
        }
    }

    private void OnProgress(PythonInstallProgress p)
    {
        Phase = p.Phase;
        OnPropertyChanged(nameof(PhaseText));
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(VersionsText));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(ShowNoSystemPython));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(PhaseText));
        InstallCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RaiseAll()
    {
        RaiseState();
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(VersionsText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
    }

    partial void OnStateChanged(PythonEnvState value) => RaiseState();
    partial void OnIsBusyChanged(bool value) => RaiseState();
    partial void OnErrorCodeChanged(string? value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ErrorText)); RaiseState(); }
}
