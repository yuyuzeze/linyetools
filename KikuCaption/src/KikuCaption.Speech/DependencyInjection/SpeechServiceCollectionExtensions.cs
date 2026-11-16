using KikuCaption.Core.Interfaces;
using KikuCaption.Speech.SpeechCpp;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KikuCaption.Speech.DependencyInjection;

/// <summary>
/// Registers the speech services. The recognizer and worker are transient (each recognition
/// session gets a fresh worker process); a factory is exposed for the UI to create sessions.
/// The native speech.cpp backend is registered only when <paramref name="speechCppOptions"/> is
/// supplied; otherwise the engine factory always resolves the Whisper recognizer.
/// </summary>
public static class SpeechServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionSpeech(
        this IServiceCollection services,
        WhisperWorkerOptions workerOptions,
        SpeechCppWorkerOptions? speechCppOptions = null)
    {
        // Whisper backend (default).
        services.AddSingleton(workerOptions);
        services.AddTransient<IWhisperWorker, ProcessWhisperWorker>();
        services.AddTransient<PythonSpeechRecognizer>();
        services.AddTransient<ISpeechRecognizer, PythonSpeechRecognizer>();
        services.AddSingleton<Func<ISpeechRecognizer>>(sp => () => sp.GetRequiredService<ISpeechRecognizer>());

        // speech.cpp backend (optional). When configured, the exe-probe environment marks it
        // available; otherwise the default environment keeps the Whisper-only fallback.
        if (speechCppOptions is not null)
        {
            services.AddSingleton(speechCppOptions);
            services.AddTransient<ISpeechCppWorker, ProcessSpeechCppWorker>();
            services.AddTransient<SpeechCppSpeechRecognizer>();
            services.TryAddSingleton<ISpeechCppEnvironment>(_ => new FileProbeSpeechCppEnvironment(speechCppOptions));
        }

        services.TryAddSingleton<ISpeechCppEnvironment, UnavailableSpeechCppEnvironment>();

        services.AddSingleton<ISpeechRecognizerFactory>(sp => new SpeechRecognizerFactory(
            whisper: () => sp.GetRequiredService<ISpeechRecognizer>(),
            speechCpp: () => sp.GetRequiredService<SpeechCppSpeechRecognizer>(),
            environment: sp.GetRequiredService<ISpeechCppEnvironment>(),
            logger: sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SpeechRecognizerFactory>>()));

        // The prewarmer picks the backend per session via the factory (engine chosen from the options).
        services.AddSingleton(sp => new SpeechRecognizerPrewarmer(
            options => sp.GetRequiredService<ISpeechRecognizerFactory>().Create(options),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SpeechRecognizerPrewarmer>>()));
        return services;
    }
}
