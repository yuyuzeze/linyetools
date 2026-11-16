namespace KikuCaption.Speech.SpeechCpp;

/// <summary>Location and launch settings for the native <c>speech.exe</c> worker process.</summary>
public sealed class SpeechCppWorkerOptions
{
    /// <summary>Absolute path to <c>speech.exe</c>.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Device for the worker (<c>auto</c>/<c>cpu</c>/<c>gpu</c>/a name). Default <c>cpu</c>.</summary>
    public string Device { get; init; } = "cpu";

    /// <summary>CPU threads (null = speech.cpp's default: the machine's performance cores).</summary>
    public int? Threads { get; init; }

    /// <summary>How long to wait for a graceful exit before killing the process tree.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded capacity of the incoming message channel (back-pressure).</summary>
    public int IncomingCapacity { get; init; } = 256;
}
