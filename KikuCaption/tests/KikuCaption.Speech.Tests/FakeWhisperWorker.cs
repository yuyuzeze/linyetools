using System.Threading.Channels;
using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.Worker;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// In-memory fake worker: reacts to sent messages by emitting protocol responses, without any
/// Python process or model. Lets the recognizer be tested deterministically and fast.
/// </summary>
internal sealed class FakeWhisperWorker : IWhisperWorker
{
    private readonly Channel<ProtocolMessage> _out = Channel.CreateUnbounded<ProtocolMessage>();
    private readonly List<ProtocolMessage> _sent = new();
    private readonly object _gate = new();
    private long _outSeq = 100;

    public bool RespondReady { get; set; } = true;
    public string? InitErrorCode { get; set; }
    public double ModelLoadMs { get; set; } = 12.3;
    public List<(double Start, double End, string Text, double? Confidence)> Finals { get; } = new();

    public int InitializeCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }

    public IReadOnlyList<ProtocolMessage> Sent
    {
        get { lock (_gate) { return _sent.ToList(); } }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(message);
        }

        switch (message.Type)
        {
            case ProtocolConstants.Types.Initialize:
                InitializeCount++;
                if (InitErrorCode is not null)
                {
                    Emit(new ProtocolMessage
                    {
                        Type = ProtocolConstants.Types.Error, SessionId = message.SessionId,
                        Seq = NextSeq(), Code = InitErrorCode, Message = "init failed"
                    });
                }
                else if (RespondReady)
                {
                    Emit(new ProtocolMessage
                    {
                        Type = ProtocolConstants.Types.Ready, SessionId = message.SessionId,
                        Seq = NextSeq(), ModelLoadMs = ModelLoadMs
                    });
                }

                break;

            case ProtocolConstants.Types.Flush:
                foreach (var f in Finals)
                {
                    Emit(new ProtocolMessage
                    {
                        Type = ProtocolConstants.Types.Partial, SessionId = message.SessionId,
                        Seq = NextSeq(), Start = f.Start, End = f.End, Text = f.Text
                    });
                }

                foreach (var f in Finals)
                {
                    Emit(new ProtocolMessage
                    {
                        Type = ProtocolConstants.Types.FinalCandidate, SessionId = message.SessionId,
                        Seq = NextSeq(), Start = f.Start, End = f.End, Text = f.Text, Confidence = f.Confidence
                    });
                }

                Emit(new ProtocolMessage
                {
                    Type = ProtocolConstants.Types.Flushed, SessionId = message.SessionId,
                    Seq = NextSeq(), Count = Finals.Count
                });
                break;

            case ProtocolConstants.Types.Shutdown:
                SimulateExit(0);
                break;
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<ProtocolMessage> ReadMessagesAsync(CancellationToken cancellationToken)
        => _out.Reader.ReadAllAsync(cancellationToken);

    public string DrainStandardError() => string.Empty;

    public void Emit(ProtocolMessage message) => _out.Writer.TryWrite(message);

    public void SimulateExit(int exitCode)
    {
        HasExited = true;
        ExitCode = exitCode;
        _out.Writer.TryComplete();
    }

    private long NextSeq() => Interlocked.Increment(ref _outSeq);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        HasExited = true;
        _out.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
