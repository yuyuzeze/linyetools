namespace KikuCaption.Audio.Capture;

public enum AudioRecorderState
{
    Idle,
    Capturing,
    Stopping,
    Stopped,
    Faulted
}

public sealed class AudioRecorderFaultedEventArgs : EventArgs
{
    public AudioRecorderFaultedEventArgs(Exception exception) => Exception = exception;

    public Exception Exception { get; }

    /// <summary>User-safe message (never contains PCM data).</summary>
    public string Message => Exception.Message;
}

/// <summary>
/// Milestone 1 validation entry: captures system audio to a WAV file. Provides explicit
/// start/stop, live metrics and a fault signal, and never blocks the UI thread. Consuming
/// code (the WPF view model) depends only on this abstraction, not on WASAPI.
/// </summary>
public interface ISystemAudioWavRecorder : IAsyncDisposable
{
    AudioRecorderState State { get; }
    TimeSpan Elapsed { get; }
    long BytesWritten { get; }
    string? OutputPath { get; }

    /// <summary>Raised (on a thread-pool thread) if capture fails asynchronously.</summary>
    event EventHandler<AudioRecorderFaultedEventArgs>? Faulted;

    /// <summary>Starts capture to a new WAV file. Refuses to overwrite an existing file.</summary>
    Task StartAsync(string outputFilePath, CancellationToken cancellationToken = default);

    /// <summary>Stops capture and finalizes the WAV file. Idempotent.</summary>
    Task StopAsync();
}
