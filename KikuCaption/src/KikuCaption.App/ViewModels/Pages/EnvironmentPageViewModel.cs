using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels.Pages;

/// <summary>
/// Environment detail page and the single source of truth for the top-bar health indicator
/// (UI-R1 §4, §5). Runs the environment check off the UI thread, aggregates green/yellow/red, and
/// exposes a sanitized "copy diagnostics" that never includes secrets, caption text, prompts or
/// hotwords.
/// </summary>
public partial class EnvironmentPageViewModel : ObservableObject
{
    // Preferred display order on the page (independent of probe registration order).
    private static readonly IReadOnlyDictionary<DependencyKind, int> DisplayOrder = new Dictionary<DependencyKind, int>
    {
        [DependencyKind.DotNetRuntime] = 0,
        [DependencyKind.Python] = 1,
        [DependencyKind.WhisperWorker] = 2,
        [DependencyKind.WhisperModel] = 3,
        [DependencyKind.AudioOutputDevice] = 4,
        [DependencyKind.FFmpeg] = 5,
        [DependencyKind.FFprobe] = 6,
        [DependencyKind.OutputDirectory] = 7,
        [DependencyKind.DiskSpace] = 8,
        [DependencyKind.TranslationApi] = 9
    };

    private readonly IEnvironmentChecker _environmentChecker;
    private readonly LocalizationService _localization;
    private readonly ILogger<EnvironmentPageViewModel> _logger;

    public EnvironmentPageViewModel(IEnvironmentChecker environmentChecker, LocalizationService localization, ILogger<EnvironmentPageViewModel> logger)
    {
        _environmentChecker = environmentChecker;
        _localization = localization;
        _logger = logger;

        _overallMessage = _localization["Env.Msg.NotChecked"];
        _overallKey = "Env.Msg.NotChecked";

        // Refresh the localized labels/messages when the UI language changes.
        _localization.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HealthText));
            if (_overallKey is not null)
            {
                OverallMessage = _localization[_overallKey];
            }
            RebuildItems(); // re-localize the per-dependency names + status badges
        };
    }

    private string? _overallKey;
    private IReadOnlyList<DependencyCheckResult> _lastResults = System.Array.Empty<DependencyCheckResult>();

    private void RebuildItems()
    {
        if (_lastResults.Count == 0)
        {
            return;
        }

        Items.Clear();
        foreach (var result in Ordered(_lastResults))
        {
            Items.Add(new EnvironmentItemViewModel(result, _localization));
        }
    }

    private void SetOverall(string key)
    {
        _overallKey = key;
        OverallMessage = _localization[key];
    }

    public ObservableCollection<EnvironmentItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    [NotifyPropertyChangedFor(nameof(HealthColor))]
    private EnvironmentHealth _health = EnvironmentHealth.Unknown;

    [ObservableProperty]
    private string _overallMessage = string.Empty;

    [ObservableProperty]
    private string _lastCheckedText = string.Empty;

    /// <summary>Short status text for the top-bar indicator; always shown alongside the colour.</summary>
    public string HealthText => IsChecking
        ? _localization["Env.Health.Checking"]
        : Health switch
        {
            EnvironmentHealth.Healthy => _localization["Env.Health.Healthy"],
            EnvironmentHealth.Degraded => _localization["Env.Health.Degraded"],
            EnvironmentHealth.Blocked => _localization["Env.Health.Blocked"],
            _ => _localization["Env.Health.Unknown"]
        };

    /// <summary>Hex colour for the top-bar dot (paired with <see cref="HealthText"/>, never alone).</summary>
    public string HealthColor => IsChecking
        ? "#9E9E9E"
        : Health switch
        {
            EnvironmentHealth.Healthy => "#2E7D32",
            EnvironmentHealth.Degraded => "#F9A825",
            EnvironmentHealth.Blocked => "#C62828",
            _ => "#9E9E9E"
        };

    [RelayCommand]
    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthColor));
        SetOverall("Env.Msg.Checking");

        try
        {
            var report = await _environmentChecker.CheckAsync(cancellationToken).ConfigureAwait(true);

            _lastResults = report.Results;
            RebuildItems();

            Health = report.OverallHealth;
            SetOverall(report.OverallHealth switch
            {
                EnvironmentHealth.Healthy => "Env.Msg.Healthy",
                EnvironmentHealth.Degraded => "Env.Msg.Degraded",
                EnvironmentHealth.Blocked => "Env.Msg.Blocked",
                _ => "Env.Msg.Healthy"
            });
            HasChecked = true;
            LastCheckedText = string.Format(_localization["Env.LastChecked"], DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException)
        {
            SetOverall("Env.Msg.Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment check failed unexpectedly.");
            SetOverall("Env.Msg.Error");
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(HealthColor));
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var text = BuildSanitizedDiagnostics();
        try
        {
            Clipboard.SetText(text);
            SetOverall("Env.Copied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying diagnostics to clipboard failed.");
            SetOverall("Env.CopyFailed");
        }
    }

    /// <summary>
    /// Builds copyable diagnostics from non-sensitive facts only: dependency name, status, version,
    /// resolved path and detail. Never includes API keys, DPAPI ciphertext, caption text, prompt or
    /// hotwords content, or company API request bodies (UI-R1 §5).
    /// </summary>
    private string BuildSanitizedDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("KikuCaption 环境诊断（脱敏）");
        sb.AppendLine("应用版本: " + (typeof(EnvironmentPageViewModel).Assembly.GetName().Version?.ToString() ?? "n/a"));
        sb.AppendLine("操作系统: " + Environment.OSVersion.VersionString);
        sb.AppendLine(".NET: " + Environment.Version);
        sb.AppendLine("整体健康: " + HealthText);
        sb.AppendLine("---");
        foreach (var item in Items)
        {
            sb.Append("[").Append(item.StatusText).Append("] ").Append(item.Name);
            if (item.HasDetectedVersion) sb.Append(" | ").Append(item.DetectedVersion);
            sb.AppendLine();
            if (item.HasResolvedPath) sb.AppendLine("    路径: " + item.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(item.Detail)) sb.AppendLine("    说明: " + item.Detail);
        }
        return sb.ToString();
    }

    private static IEnumerable<DependencyCheckResult> Ordered(IEnumerable<DependencyCheckResult> results)
        => results.OrderBy(r => DisplayOrder.TryGetValue(r.Kind, out var order) ? order : int.MaxValue);
}
