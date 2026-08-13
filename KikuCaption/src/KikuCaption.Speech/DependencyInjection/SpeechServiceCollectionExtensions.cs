using KikuCaption.Core.Interfaces;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace KikuCaption.Speech.DependencyInjection;

/// <summary>
/// Registers the speech services. The recognizer and worker are transient (each recognition
/// session gets a fresh worker process); a factory is exposed for the UI to create sessions.
/// </summary>
public static class SpeechServiceCollectionExtensions
{
    public static IServiceCollection AddKikuCaptionSpeech(
        this IServiceCollection services,
        WhisperWorkerOptions workerOptions)
    {
        services.AddSingleton(workerOptions);
        services.AddTransient<IWhisperWorker, ProcessWhisperWorker>();
        services.AddTransient<ISpeechRecognizer, PythonSpeechRecognizer>();
        services.AddSingleton<Func<ISpeechRecognizer>>(sp => () => sp.GetRequiredService<ISpeechRecognizer>());
        services.AddSingleton<SpeechRecognizerPrewarmer>();
        return services;
    }
}
