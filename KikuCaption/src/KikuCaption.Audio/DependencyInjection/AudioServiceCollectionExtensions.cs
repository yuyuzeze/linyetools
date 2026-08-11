using KikuCaption.Audio.Capture;
using KikuCaption.Audio.Diagnostics;
using KikuCaption.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace KikuCaption.Audio.DependencyInjection;

/// <summary>
/// Registers the audio capture services. The recorder is a singleton (holds session state);
/// the capture service is transient because each capture session needs a fresh WASAPI object,
/// obtained through the injected factory.
/// </summary>
public static class AudioServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionAudio(this IServiceCollection services)
    {
        services.AddTransient<WasapiLoopbackAudioCaptureService>();
        services.AddTransient<IAudioCaptureService>(sp =>
            sp.GetRequiredService<WasapiLoopbackAudioCaptureService>());
        services.AddSingleton<Func<IAudioCaptureService>>(sp =>
            () => sp.GetRequiredService<IAudioCaptureService>());
        services.AddSingleton<ISystemAudioWavRecorder, SystemAudioWavRecorder>();
        services.AddSingleton<IAudioDeviceInfoProvider, AudioDeviceInfoProvider>();
        return services;
    }
}
