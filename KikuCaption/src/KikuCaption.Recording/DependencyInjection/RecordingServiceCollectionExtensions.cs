using KikuCaption.Core.Interfaces;
using KikuCaption.Recording.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Recording.DependencyInjection;

public static class RecordingServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionRecording(this IServiceCollection services)
    {
        services.AddSingleton<FFmpegCapabilityProbe>();
        services.AddTransient<IScreenRecorder>(sp => new FFmpegScreenRecorder(
            () => sp.GetRequiredService<IAudioCaptureService>(),
            sp.GetRequiredService<ILogger<FFmpegScreenRecorder>>()));
        services.AddSingleton<Func<IScreenRecorder>>(sp => () => sp.GetRequiredService<IScreenRecorder>());
        return services;
    }
}
