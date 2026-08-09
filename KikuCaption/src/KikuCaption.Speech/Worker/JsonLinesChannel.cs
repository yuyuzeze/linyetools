using System.Threading.Channels;
using KikuCaption.Speech.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.Speech.Worker;

/// <summary>
/// JSON Lines transport over a reader (worker stdout) and a writer (worker stdin).
///
/// * A single background loop reads stdout — only one consumer of the worker's output.
/// * <see cref="SendAsync"/> serializes writes with a semaphore so concurrent sends never
///   interleave a JSON line.
/// * Parsed messages go into a bounded channel, so a slow consumer applies back-pressure to
///   the worker (via the OS pipe) instead of growing memory without bound.
/// </summary>
public sealed class JsonLinesChannel : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<ProtocolMessage> _incoming;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    public JsonLinesChannel(TextReader reader, TextWriter writer, int capacity = 256, ILogger? logger = null)
    {
        _reader = reader;
        _writer = writer;
        _logger = logger ?? NullLogger.Instance;
        _incoming = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        var line = JsonLinesCodec.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IAsyncEnumerable<ProtocolMessage> ReadMessagesAsync(CancellationToken cancellationToken)
        => _incoming.Reader.ReadAllAsync(cancellationToken);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                ProtocolMessage message;
                try
                {
                    message = JsonLinesCodec.Parse(line);
                }
                catch (ProtocolException ex)
                {
                    // A malformed line on stdout is a worker bug; log and skip, don't crash.
                    _logger.LogWarning("Ignoring malformed worker stdout line: {Code}", ex.Code);
                    continue;
                }

                await _incoming.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }

            _incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _incoming.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _incoming.Writer.TryComplete(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _incoming.Writer.TryComplete();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
