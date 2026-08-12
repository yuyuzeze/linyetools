using KikuCaption.Audio.Capture;
using KikuCaption.Audio.Diagnostics;
using KikuCaption.Audio.Mixing;
using KikuCaption.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // UI-R5A: a session mixer (one loopback + optional mic → fan-out) built per meeting from the
        // chosen inputs, and a transient live input-level meter for the start dialog.
        services.AddSingleton<Func<AudioMixOptions, SessionAudioMixer>>(sp => options =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var system = options.RecordSystemAudio ? sp.GetRequiredService<IAudioCaptureService>() : null;
            var mic = options.RecordMicrophone
                ? new MicrophoneCaptureService(options.MicrophoneDeviceId, loggerFactory.CreateLogger<MicrophoneCaptureService>())
                : null;
            return new SessionAudioMixer(system, mic, loggerFactory.CreateLogger<SessionAudioMixer>());
        });
        services.AddTransient<MicrophoneLevelMeter>();
        services.AddSingleton<Func<MicrophoneLevelMeter>>(sp => () => sp.GetRequiredService<MicrophoneLevelMeter>());

        return services;
    }
}
