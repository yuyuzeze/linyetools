namespace KikuCaption.Speech.Worker;

/// <summary>Locations and timeouts for launching the Python worker process.</summary>
public sealed class WhisperWorkerOptions
{
    /// <summary>Path to the venv Python executable.</summary>
    public required string PythonExecutable { get; init; }

    /// <summary>Path to <c>main.py</c>.</summary>
    public required string WorkerScript { get; init; }

    /// <summary>Explicit, discoverable model cache directory (passed as --download-root).</summary>
    public string? ModelCacheDirectory { get; init; }

    /// <summary>How long to wait for a graceful exit before killing the process tree.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Bounded capacity of the incoming message channel (back-pressure).</summary>
    public int IncomingCapacity { get; init; } = 256;
}
