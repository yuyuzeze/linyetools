using KikuCaption.Speech.Protocol;

namespace KikuCaption.Speech.Worker;

/// <summary>
/// Abstraction over the resident worker process transport. Lets the recognizer be unit tested
/// with a fake worker (no Python, no model). Exactly one consumer reads
/// <see cref="ReadMessagesAsync"/>; <see cref="SendAsync"/> is safe to call concurrently
/// (writes are serialized).
/// </summary>
public interface IWhisperWorker : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken);

    IAsyncEnumerable<ProtocolMessage> ReadMessagesAsync(CancellationToken cancellationToken);

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Returns a snapshot of recent worker stderr diagnostics (never contains PCM).</summary>
    string DrainStandardError();
}
