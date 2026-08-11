using System.Collections.Generic;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.Core.Interfaces;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Translation;
using KikuCaption.Translation.Security;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Translation settings + status panel (M6 §9). Binds to the live <see cref="TranslationOptions"/>
/// singleton, manages the DPAPI-stored API key (save/clear, never echoed), runs a fixed-text Test
/// Connection, and shows queue/last-error status. The API key never flows through data-binding or
/// logs; it is passed once to the secret store from the PasswordBox.
/// </summary>
public sealed partial class TranslationViewModel : ObservableObject
{
    private readonly TranslationOptions _options;
    private readonly ITranslationSecretStore _secrets;
    private readonly IAiTranslationService _translator;
    private readonly TranslationQueue _queue;
    private readonly UserSettingsStore _settingsStore;
    private readonly ILogger<TranslationViewModel> _logger;
    private readonly HashSet<Guid> _active = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _apiVersion = string.Empty;
    [ObservableProperty] private string _authenticationMode = "Bearer";
    [ObservableProperty] private string _headerName = "Authorization";
    [ObservableProperty] private string _proxy = string.Empty;
    [ObservableProperty] private int _timeoutSeconds = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyStatusText))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    private bool _isKeyConfigured;

    [ObservableProperty] private string _testStatus = string.Empty;
    [ObservableProperty] private bool _testInProgress;
    [ObservableProperty] private string _queueStatus = "空闲";
    [ObservableProperty] private string _lastErrorText = string.Empty;

    public TranslationViewModel(
        TranslationOptions options,
        ITranslationSecretStore secrets,
        IAiTranslationService translator,
        TranslationQueue queue,
        UserSettingsStore settingsStore,
        ILogger<TranslationViewModel> logger)
    {
        _options = options;
        _secrets = secrets;
        _translator = translator;
        _queue = queue;
        _settingsStore = settingsStore;
        _logger = logger;

        // Seed from the current options (already overlaid with persisted settings at startup).
        _enabled = options.Enabled;
        _endpoint = options.Endpoint;
        _model = options.Model;
        _apiVersion = options.ApiVersion;
        _authenticationMode = options.AuthenticationMode.ToString();
        _headerName = options.HeaderName;
        _proxy = options.Proxy;
        _timeoutSeconds = options.TimeoutSeconds;
        _isKeyConfigured = _secrets.IsConfigured;

        _queue.OutcomeChanged += OnOutcomeChanged;
    }

    public IReadOnlyList<string> AuthModes { get; } = new[] { "Bearer", "ApiKeyHeader", "None" };

    public string KeyStatusText => IsKeyConfigured ? "API Key：已配置" : "API Key：未配置";

    /// <summary>
    /// Display-only current translation direction, e.g. "日本語 → 中文" (UI-R2 home quick control).
    /// Derived from the configured source/target languages; the dynamic "source follows recognition
    /// language" behaviour and target selection are UI-R4, not implemented here.
    /// </summary>
    public string DirectionText => $"{LanguageName(_options.SourceLanguage)} → {LanguageName(_options.TargetLanguage)}";

    /// <summary>True when a usable translation configuration exists (endpoint + model, and a key unless auth is None).</summary>
    public bool IsConfigured =>
        Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_options.Model)
        && (_options.AuthenticationMode == TranslationAuthMode.None || IsKeyConfigured);

    private static string LanguageName(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "ja" => "日本語",
        "zh" => "中文",
        "en" => "English",
        _ => code ?? string.Empty
    };

    // Keep the live options in sync as the user edits the panel.
    partial void OnEnabledChanged(bool value) => _options.Enabled = value;
    partial void OnEndpointChanged(string value) => _options.Endpoint = value?.Trim() ?? string.Empty;
    partial void OnModelChanged(string value) => _options.Model = value?.Trim() ?? string.Empty;
    partial void OnApiVersionChanged(string value) => _options.ApiVersion = value?.Trim() ?? string.Empty;
    partial void OnHeaderNameChanged(string value) => _options.HeaderName = string.IsNullOrWhiteSpace(value) ? "Authorization" : value.Trim();
    partial void OnProxyChanged(string value) => _options.Proxy = value?.Trim() ?? string.Empty;
    partial void OnTimeoutSecondsChanged(int value) => _options.TimeoutSeconds = value;

    /// <summary>
    /// Saves the translation config so it is restored next launch. Non-secret fields go to
    /// settings.json; the Endpoint (which may embed a credential) is DPAPI-encrypted. The API key is
    /// saved separately via <see cref="SaveApiKey"/>.
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var (existing, _) = _settingsStore.Load();
            _settingsStore.Save(existing with
            {
                TranslationEnabled = Enabled,
                TranslationModel = Model?.Trim() ?? string.Empty,
                TranslationApiVersion = ApiVersion?.Trim() ?? string.Empty,
                TranslationAuthMode = AuthenticationMode,
                TranslationHeaderName = string.IsNullOrWhiteSpace(HeaderName) ? "Authorization" : HeaderName.Trim(),
                TranslationProxy = Proxy?.Trim() ?? string.Empty
            });

            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                _secrets.DeleteEndpoint();
            }
            else
            {
                _secrets.SaveEndpoint(Endpoint.Trim());
            }

            TestStatus = "翻译设置已保存（Endpoint 已加密，下次打开自动读取）。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save translation settings.");
            TestStatus = "保存翻译设置失败。";
        }
    }

    partial void OnAuthenticationModeChanged(string value)
    {
        if (Enum.TryParse<TranslationAuthMode>(value, out var mode))
        {
            _options.AuthenticationMode = mode;
        }
    }

    /// <summary>Saves the API key to the DPAPI store. Called from the PasswordBox handler only.</summary>
    public void SaveApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            TestStatus = "未输入 API Key。";
            return;
        }

        try
        {
            _secrets.Save(key);
            IsKeyConfigured = true;
            TestStatus = "已保存 API Key（已加密到本地用户存储）。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save API key.");
            TestStatus = "保存 API Key 失败。";
        }
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        try
        {
            _secrets.Delete();
            IsKeyConfigured = false;
            TestStatus = "已清除本地 API Key。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear API key.");
            TestStatus = "清除 API Key 失败。";
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestInProgress = true;
        TestStatus = "正在测试连接……";
        try
        {
            // Fixed, non-sensitive text — never a real meeting subtitle.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 60)));
            var result = await _translator.TranslateAsync("テスト接続", _options.SourceLanguage, _options.TargetLanguage, cts.Token);
            TestStatus = string.IsNullOrWhiteSpace(result) ? "连接成功，但返回为空。" : "连接成功。";
        }
        catch (TranslationException ex)
        {
            TestStatus = ex.Code switch
            {
                Core.Enums.TranslationErrorCode.Auth => "认证失败（请检查 API Key / 认证模式）。",
                Core.Enums.TranslationErrorCode.RateLimited => "被限流（429）。",
                Core.Enums.TranslationErrorCode.ServiceUnavailable => "服务暂时不可用（5xx）。",
                Core.Enums.TranslationErrorCode.Timeout => "连接超时。",
                Core.Enums.TranslationErrorCode.Network => "网络错误。",
                Core.Enums.TranslationErrorCode.InvalidConfig => "配置无效（Endpoint / HTTPS / Key）。",
                _ => "连接失败：" + ex.Code
            };
            // Never log the key or response body.
            _logger.LogWarning("Test connection failed ({Code}).", ex.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test connection error.");
            TestStatus = "连接失败。";
        }
        finally
        {
            TestInProgress = false;
        }
    }

    private void OnOutcomeChanged(object? sender, TranslationOutcome outcome)
    {
        Dispatch(() =>
        {
            switch (outcome.State)
            {
                case Core.Enums.TranslationJobState.Pending:
                case Core.Enums.TranslationJobState.InProgress:
                case Core.Enums.TranslationJobState.RetryScheduled:
                    _active.Add(outcome.SegmentId);
                    break;
                default:
                    _active.Remove(outcome.SegmentId);
                    break;
            }

            if (outcome.ErrorCode != Core.Enums.TranslationErrorCode.None)
            {
                LastErrorText = "最近错误：" + outcome.ErrorCode;
            }

            QueueStatus = _active.Count == 0 ? "空闲" : $"翻译队列：{_active.Count} 条处理中";
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
