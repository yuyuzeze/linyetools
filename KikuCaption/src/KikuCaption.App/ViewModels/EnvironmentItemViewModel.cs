using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Presentation wrapper around a single <see cref="DependencyCheckResult"/>.
/// </summary>
public sealed class EnvironmentItemViewModel
{
    private readonly DependencyCheckResult _result;

    public EnvironmentItemViewModel(DependencyCheckResult result) => _result = result;

    public string Name => _result.Name;

    public string StatusText => _result.Status switch
    {
        EnvironmentCheckStatus.Ok => "正常",
        EnvironmentCheckStatus.Warning => "注意",
        EnvironmentCheckStatus.Missing => "缺失",
        EnvironmentCheckStatus.Error => "错误",
        _ => "未知"
    };

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
    public string? Detail => _result.Detail;
    public string? Remediation => _result.Remediation;
    public bool HasRemediation => !string.IsNullOrWhiteSpace(_result.Remediation);
}
