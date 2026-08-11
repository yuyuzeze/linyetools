using System.Collections.Generic;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
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
    private readonly LocalizationService _loc;
    private readonly ILogger<TranslationViewModel> _logger;
    private readonly HashSet<Guid> _active = new();

    [ObservableProperty] private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText))]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    [NotifyPropertyChangedFor(nameof(IsSameLanguage))]
    private string _targetLanguage = "zh";

    /// <summary>True when the (recognition-following) source equals the target — nothing to translate.</summary>
    public bool IsSameLanguage =>
        string.Equals(_options.SourceLanguage, TargetLanguage, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _apiVersion = string.Empty;
    [ObservableProperty] private string _authenticationMode = "Bearer";
    [ObservableProperty] private string _headerName = "Authorization";
    [ObservableProperty] private string _proxy = string.Empty;
    [ObservableProperty] private int _timeoutSeconds = 30;
    [ObservableProperty] private int _maxRetries = 3;

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
        LocalizationService localization,
        ILogger<TranslationViewModel> logger)
    {
        _options = options;
        _secrets = secrets;
        _translator = translator;
        _queue = queue;
        _settingsStore = settingsStore;
        _loc = localization;
        _logger = logger;
        _loc.LanguageChanged += (_, _) => Dispatch(() =>
        {
            OnPropertyChanged(nameof(DirectionText));
            OnPropertyChanged(nameof(KeyStatusText));
            RefreshQueueStatus();
        });

        // Seed from the current options (already overlaid with persisted settings at startup).
        _enabled = options.Enabled;
        _endpoint = options.Endpoint;
        _model = options.Model;
        _apiVersion = options.ApiVersion;
        _authenticationMode = options.AuthenticationMode.ToString();
        _headerName = options.HeaderName;
        _proxy = options.Proxy;
        _timeoutSeconds = options.TimeoutSeconds;
        _maxRetries = options.MaxRetries;
        _targetLanguage = string.IsNullOrWhiteSpace(options.TargetLanguage) ? "zh" : options.TargetLanguage;
        _isKeyConfigured = _secrets.IsConfigured;

        _queue.OutcomeChanged += OnOutcomeChanged;
        RefreshQueueStatus(); // localized initial "Idle"
    }

    public IReadOnlyList<string> AuthModes { get; } = new[] { "Bearer", "ApiKeyHeader", "None" };

    /// <summary>Selectable target languages (stable codes; display is localized in the view).</summary>
    public IReadOnlyList<string> TargetLanguages { get; } = new[] { "zh", "en", "ja" };

    // Keep the live options + persisted setting in sync as the user picks a target (UI-R4A).
    partial void OnTargetLanguageChanged(string value) => _options.TargetLanguage = string.IsNullOrWhiteSpace(value) ? "zh" : value;

    public string KeyStatusText => _loc[IsKeyConfigured ? "Tr.KeyConfigured" : "Tr.KeyNotConfigured"];

    /// <summary>
    /// Display-only current translation direction, e.g. "日本語 → 中文" (UI-R2 home quick control).
    /// Derived from the configured source/target languages; the dynamic "source follows recognition
    /// language" behaviour and target selection are UI-R4, not implemented here.
    /// </summary>
    public string DirectionText => $"{_loc["Lang." + (_options.SourceLanguage ?? string.Empty).ToLowerInvariant()]} → {_loc["Lang." + (_options.TargetLanguage ?? string.Empty).ToLowerInvariant()]}";

    /// <summary>True when a usable translation configuration exists (endpoint + model, and a key unless auth is None).</summary>
    public bool IsConfigured =>
        Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(_options.Model)
        && (_options.AuthenticationMode == TranslationAuthMode.None || IsKeyConfigured);


    // Keep the live options in sync as the user edits the panel.
    partial void OnEnabledChanged(bool value) => _options.Enabled = value;
    partial void OnEndpointChanged(string value) => _options.Endpoint = value?.Trim() ?? string.Empty;
    partial void OnModelChanged(string value) => _options.Model = value?.Trim() ?? string.Empty;
    partial void OnApiVersionChanged(string value) => _options.ApiVersion = value?.Trim() ?? string.Empty;
    partial void OnHeaderNameChanged(string value) => _options.HeaderName = string.IsNullOrWhiteSpace(value) ? "Authorization" : value.Trim();
    partial void OnProxyChanged(string value) => _options.Proxy = value?.Trim() ?? string.Empty;
    // Clamp to valid ranges so an out-of-range value can never be applied or saved (UI-R4A fix).
    // A non-numeric TextBox entry fails WPF binding validation and leaves the property unchanged
    // (no crash); these clamps guard the numeric range.
    partial void OnTimeoutSecondsChanged(int value) => _options.TimeoutSeconds = Math.Clamp(value, 1, 300);
    partial void OnMaxRetriesChanged(int value) => _options.MaxRetries = Math.Clamp(value, 0, 10);

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
                TranslationProxy = Proxy?.Trim() ?? string.Empty,
                TranslationTargetLanguage = string.IsNullOrWhiteSpace(TargetLanguage) ? "zh" : TargetLanguage,
                TranslationTimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 300),
                TranslationMaxRetries = Math.Clamp(MaxRetries, 0, 10)
            });

            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                _secrets.DeleteEndpoint();
            }
            else
            {
                _secrets.SaveEndpoint(Endpoint.Trim());
            }

            TestStatus = _loc["Tr.SettingsSaved"];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save translation settings.");
            TestStatus = _loc["Tr.SettingsSaveFailed"];
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
        // Same-language: nothing to translate — validate config / ask for a different target, no call.
        if (string.Equals(_options.SourceLanguage, _options.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            TestStatus = _loc["Tr.TestSameLanguage"];
            return;
        }

        TestInProgress = true;
        TestStatus = _loc["Tr.Test.Testing"];
        try
        {
            // Fixed, non-sensitive text — never a real meeting subtitle. Validates the request format
            // for the CURRENT source/target direction.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 60)));
            // Current prompt version (v2), current source/target, and the configured model.
            var request = new TranslationRequest("connection test", _options.SourceLanguage, _options.TargetLanguage,
                _options.Model, TranslationPrompt.Version);
            var result = await _translator.TranslateAsync(request, cts.Token);
            TestStatus = _loc[string.IsNullOrWhiteSpace(result) ? "Tr.Test.Empty" : "Tr.Test.Success"];
        }
        catch (TranslationException ex)
        {
            TestStatus = _loc[ex.Code switch
            {
                Core.Enums.TranslationErrorCode.Auth => "Tr.Test.Auth",
                Core.Enums.TranslationErrorCode.RateLimited => "Tr.Test.RateLimited",
                Core.Enums.TranslationErrorCode.ServiceUnavailable => "Tr.Test.Unavailable",
                Core.Enums.TranslationErrorCode.Timeout => "Tr.Test.Timeout",
                Core.Enums.TranslationErrorCode.Network => "Tr.Test.Network",
                Core.Enums.TranslationErrorCode.InvalidConfig => "Tr.Test.InvalidConfig",
                _ => "Tr.Test.Failed"
            }];
            // Never log the key or response body.
            _logger.LogWarning("Test connection failed ({Code}).", ex.Code);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test connection error.");
            TestStatus = _loc["Tr.Test.Failed"];
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
                LastErrorText = string.Format(_loc["Tr.LastError"], outcome.ErrorCode);
            }

            RefreshQueueStatus();
        });
    }

    // Localized queue status ("Idle" / "Translation queue: N in progress"), refreshed on outcome or
    // UI-language change.
    private void RefreshQueueStatus()
        => QueueStatus = _active.Count == 0 ? _loc["Tr.QueueIdle"] : string.Format(_loc["Tr.QueueBusy"], _active.Count);

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
