using KikuCaption.Core.Interfaces;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Infrastructure.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace KikuCaption.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the infrastructure services (configuration, validation, environment checks)
/// with the DI container. The App composes these together (PROJECT.md 7).
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind + validate configuration. The custom validator runs the first time
        // IOptions.Value is accessed, which the App forces at startup (PROJECT.md 11).
        services.AddOptions<KikuCaptionOptions>()
            .Bind(configuration);
        services.AddSingleton<IValidateOptions<KikuCaptionOptions>, KikuCaptionOptionsValidator>();

        // Process helper for probes that shell out to python/ffmpeg.
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // Environment probes + aggregating checker.
        services.AddSingleton<IEnvironmentProbe, DotNetRuntimeProbe>();
        services.AddSingleton<IEnvironmentProbe, PythonProbe>();
        services.AddSingleton<IEnvironmentProbe, FFmpegProbe>();
        services.AddSingleton<IEnvironmentProbe, FFprobeProbe>();
        services.AddSingleton<IEnvironmentProbe, DiskSpaceProbe>();
        services.AddSingleton<IEnvironmentChecker, EnvironmentChecker>();

        return services;
    }
}
