using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly ILogger<EnvironmentPageViewModel> _logger;

    public EnvironmentPageViewModel(IEnvironmentChecker environmentChecker, ILogger<EnvironmentPageViewModel> logger)
    {
        _environmentChecker = environmentChecker;
        _logger = logger;
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
    private string _overallMessage = "尚未检查运行环境。";

    [ObservableProperty]
    private string _lastCheckedText = string.Empty;

    /// <summary>Short status text for the top-bar indicator; always shown alongside the colour.</summary>
    public string HealthText => IsChecking
        ? "正在检查…"
        : Health switch
        {
            EnvironmentHealth.Healthy => "环境正常",
            EnvironmentHealth.Degraded => "部分功能受限",
            EnvironmentHealth.Blocked => "关键环境缺失",
            _ => "尚未检查"
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
        OverallMessage = "正在检查运行环境……";

        try
        {
            var report = await _environmentChecker.CheckAsync(cancellationToken).ConfigureAwait(true);

            Items.Clear();
            foreach (var result in Ordered(report.Results))
            {
                Items.Add(new EnvironmentItemViewModel(result));
            }

            Health = report.OverallHealth;
            OverallMessage = report.OverallHealth switch
            {
                EnvironmentHealth.Healthy => "运行环境检查完成，未发现问题。",
                EnvironmentHealth.Degraded => "字幕可正常使用；部分非关键能力（录屏或翻译）当前不可用（详见下方）。",
                EnvironmentHealth.Blocked => "缺少关键依赖，核心字幕功能暂时无法开始（详见下方）。程序不会崩溃。",
                _ => "运行环境检查完成。"
            };
            HasChecked = true;
            LastCheckedText = "最近检查：" + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (OperationCanceledException)
        {
            OverallMessage = "环境检查已取消。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment check failed unexpectedly.");
            OverallMessage = "环境检查过程出错，详见日志。";
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
            OverallMessage = "已复制诊断信息（不含任何密钥或字幕内容）。";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copying diagnostics to clipboard failed.");
            OverallMessage = "复制诊断信息失败，请重试。";
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
