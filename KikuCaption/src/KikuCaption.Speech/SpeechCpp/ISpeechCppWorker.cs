namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// Transport over a resident <c>speech.exe worker MODEL</c> process (speech.cpp protocol 4). Lets
/// the recognizer be unit tested with a fake worker (no process). Exactly one consumer reads
/// <see cref="ReadMessagesAsync"/>; <see cref="SendAsync"/> serializes writes so lines never
/// interleave. Unlike the Whisper worker, the model is a launch argument, so it is passed to
/// <see cref="StartAsync"/> rather than sent as a message.
/// </summary>
public interface ISpeechCppWorker : IAsyncDisposable
{
    Task StartAsync(string model, CancellationToken cancellationToken);

    Task SendAsync(SpeechCppMessage message, CancellationToken cancellationToken);

    IAsyncEnumerable<SpeechCppMessage> ReadMessagesAsync(CancellationToken cancellationToken);

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Snapshot of recent worker stderr diagnostics (never contains PCM).</summary>
    string DrainStandardError();
}
