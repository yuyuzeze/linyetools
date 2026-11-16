namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// Reports whether the native speech.cpp backend can run (the <c>speech.exe</c> is present and a
/// worker options object is configured). Implemented in the app layer by the speech.cpp locator;
/// the factory uses it to decide between SpeechCpp and the Whisper fallback. A default
/// implementation that is always unavailable keeps the Whisper path working when speech.cpp is not
/// installed.
/// </summary>
public interface ISpeechCppEnvironment
{
    bool IsAvailable { get; }
}

/// <summary>Default: speech.cpp is not available (Whisper-only). Replaced in the app by the locator.</summary>
public sealed class UnavailableSpeechCppEnvironment : ISpeechCppEnvironment
{
    public bool IsAvailable => false;
}

/// <summary>Availability by a simple presence check of the configured <c>speech.exe</c> path.</summary>
public sealed class FileProbeSpeechCppEnvironment : ISpeechCppEnvironment
{
    private readonly SpeechCppWorkerOptions _options;

    public FileProbeSpeechCppEnvironment(SpeechCppWorkerOptions options) => _options = options;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ExecutablePath)
        && System.IO.File.Exists(_options.ExecutablePath);
}
