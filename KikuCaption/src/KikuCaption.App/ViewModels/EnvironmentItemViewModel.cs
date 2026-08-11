using KikuCaption.App.Localization;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Presentation wrapper around a single <see cref="DependencyCheckResult"/>. The dependency name and
/// status badge are localized (UI-R3); the badge is always text (never colour alone) for
/// colour-vision accessibility (UI-R1 §4). The probe-produced Detail/Remediation sentences are shown
/// as-is (their localization is a probe-layer task).
/// </summary>
public sealed class EnvironmentItemViewModel
{
    private readonly DependencyCheckResult _result;

    public EnvironmentItemViewModel(DependencyCheckResult result) => _result = result;

    public DependencyKind Kind => _result.Kind;

    /// <summary>Localized dependency name (falls back to the probe's name if no key exists).</summary>
    public string Name
    {
        get
        {
            var key = "Env.Dep." + _result.Kind;
            var localized = LocalizationService.Instance[key];
            return localized == key ? _result.Name : localized;
        }
    }

    public string StatusText => LocalizationService.Instance["Env.Status." + _result.Status];

    /// <summary>Hex colour bound directly to a WPF Brush target (string→Brush conversion).</summary>
    public string StatusColor => _result.Status switch
    {
        EnvironmentCheckStatus.Ok => "#2E7D32",
        EnvironmentCheckStatus.Warning => "#F9A825",
        EnvironmentCheckStatus.Missing => "#C62828",
        EnvironmentCheckStatus.Error => "#B71C1C",
        _ => "#616161"
    };

    public string? DetectedVersion => _result.DetectedVersion;
    public bool HasDetectedVersion => !string.IsNullOrWhiteSpace(_result.DetectedVersion);

    public string? Detail => _result.Detail;

    public string? ResolvedPath => _result.ResolvedPath;
    public bool HasResolvedPath => !string.IsNullOrWhiteSpace(_result.ResolvedPath);

    public string? Remediation => _result.Remediation;
    public bool HasRemediation => !string.IsNullOrWhiteSpace(_result.Remediation);
}
