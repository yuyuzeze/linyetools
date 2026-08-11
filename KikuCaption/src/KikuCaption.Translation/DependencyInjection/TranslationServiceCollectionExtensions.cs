using System.Net.Http;
using KikuCaption.Core.Interfaces;
using KikuCaption.Translation.Security;
using Microsoft.Extensions.DependencyInjection;

namespace KikuCaption.Translation.DependencyInjection;

/// <summary>
/// Registers the company translation adapter, DPAPI secret store, and bounded queue. A single
/// reusable <see cref="System.Net.Http.HttpClient"/> is created via <c>IHttpClientFactory</c> — never
/// per subtitle. The App supplies <see cref="ITranslationJobStore"/> (the SQLite store).
/// </summary>
public static class TranslationServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionTranslation(
        this IServiceCollection services, TranslationOptions options, string secretsDirectory)
    {
        services.AddSingleton(options);
        services.AddSingleton<ITranslationSecretStore>(_ => new DpapiTranslationSecretStore(secretsDirectory));

        // One reusable, pooled client for all translation requests. Its primary handler carries a
        // dynamic proxy (TranslationOptions.Proxy, live) and otherwise uses the system proxy.
        services.AddHttpClient(OpenAiCompatibleTranslationAdapter.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new OptionsWebProxy(sp.GetRequiredService<TranslationOptions>())
            });

        services.AddSingleton<IAiTranslationService, OpenAiCompatibleTranslationAdapter>();
        services.AddSingleton<TranslationQueue>();
        services.AddSingleton<ITranslationQueue>(sp => sp.GetRequiredService<TranslationQueue>());
        return services;
    }
}
