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
            return Result(EnvironmentCheckStatus.Ok, "EnvMsg.Trans.Disabled", null);
        }

        var endpointOk = Uri.TryCreate(_translation.Endpoint, UriKind.Absolute, out var u)
                         && u.Scheme == Uri.UriSchemeHttps;
        var modelOk = !string.IsNullOrWhiteSpace(_translation.Model);
        var keyOk = _translation.AuthenticationMode == TranslationAuthMode.None || KeyConfigured();

        if (endpointOk && modelOk && keyOk)
        {
            return Result(EnvironmentCheckStatus.Ok, "EnvMsg.Trans.Ok", null);
        }

        // The missing field names are raw config identifiers (not translated).
        var missing = new List<string>();
        if (!endpointOk) missing.Add("Endpoint");
        if (!modelOk) missing.Add("Model");
        if (!keyOk) missing.Add("API Key");

        return Result(EnvironmentCheckStatus.Warning, "EnvMsg.Trans.Incomplete", "EnvRem.Trans.Incomplete",
            new[] { string.Join("、", missing) });
    }

    private bool KeyConfigured()
    {
        try { return _secrets.IsConfigured; }
        catch { return false; }
    }

    private Task<DependencyCheckResult> Result(EnvironmentCheckStatus status, string messageCode, string? remediationCode, IReadOnlyList<string>? messageArgs = null)
        => Task.FromResult(new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = false,
            Status = status,
            MessageCode = messageCode,
            MessageArguments = messageArgs,
            RemediationCode = remediationCode
        });
}
