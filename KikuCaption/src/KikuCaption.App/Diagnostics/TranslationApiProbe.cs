using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Translation;
using KikuCaption.Translation.Security;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Reports whether translation is configured — <em>only</em> whether, never the key, endpoint, or
/// any secret value (UI-R1 §5). Translation is optional, so an unconfigured/incomplete state is a
/// warning (yellow), never blocking. Disabled translation is a normal, healthy state.
/// </summary>
public sealed class TranslationApiProbe : IEnvironmentProbe
{
    private readonly TranslationOptions _translation;
    private readonly ITranslationSecretStore _secrets;

    public TranslationApiProbe(TranslationOptions translation, ITranslationSecretStore secrets)
    {
        _translation = translation;
        _secrets = secrets;
    }

    public DependencyKind Kind => DependencyKind.TranslationApi;
    public string DisplayName => "翻译 API 配置";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!_translation.Enabled)
        {
            return Result(EnvironmentCheckStatus.Ok, "翻译未启用（可在设置中开启）。", null, "未启用");
        }

        var endpointOk = Uri.TryCreate(_translation.Endpoint, UriKind.Absolute, out var u)
                         && u.Scheme == Uri.UriSchemeHttps;
        var modelOk = !string.IsNullOrWhiteSpace(_translation.Model);
        var keyOk = _translation.AuthenticationMode == TranslationAuthMode.None || KeyConfigured();

        if (endpointOk && modelOk && keyOk)
        {
            return Result(EnvironmentCheckStatus.Ok, "翻译已配置并可用。", null, "已配置");
        }

        var missing = new List<string>();
        if (!endpointOk) missing.Add("Endpoint");
        if (!modelOk) missing.Add("Model");
        if (!keyOk) missing.Add("API Key");

        return Result(
            EnvironmentCheckStatus.Warning,
            $"翻译已启用但配置不完整（缺少：{string.Join("、", missing)}）。字幕不受影响。",
            "请在翻译设置中补全配置；密钥通过 Windows DPAPI 本地加密保存。",
            "配置不完整");
    }

    private bool KeyConfigured()
    {
        try { return _secrets.IsConfigured; }
        catch { return false; }
    }

    private Task<DependencyCheckResult> Result(EnvironmentCheckStatus status, string detail, string? remediation, string version)
        => Task.FromResult(new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = false,
            Status = status,
            DetectedVersion = version,
            Detail = detail,
            Remediation = remediation
        });
}
