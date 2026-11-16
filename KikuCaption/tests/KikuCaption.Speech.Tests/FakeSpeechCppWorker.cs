using System.Threading.Channels;
using KikuCaption.Speech.SpeechCpp;

namespace KikuCaption.Speech.Tests;

/// <summary>
/// In-memory fake speech.cpp worker: emits <c>ready</c> on start and answers a <c>transcribe</c>
/// with <c>end</c> (or a configured <c>error</c>), without any process. Records sent lines so tests
/// can assert the chunk/transcribe mapping and the request options.
/// </summary>
internal sealed class FakeSpeechCppWorker : ISpeechCppWorker
{
    private readonly Channel<SpeechCppMessage> _out = Channel.CreateUnbounded<SpeechCppMessage>();
    private readonly List<SpeechCppMessage> _sent = new();
    private readonly object _gate = new();

    public bool RespondReady { get; set; } = true;
    public string? FatalCode { get; set; }
    public string? TranscribeErrorCode { get; set; }
    public string EndText { get; set; } = string.Empty;
    public IReadOnlyList<SpeechCppSegment>? EndSegments { get; set; }

    public string? StartedModel { get; private set; }
    public bool Disposed { get; private set; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }

    public IReadOnlyList<SpeechCppMessage> Sent
    {
        get { lock (_gate) { return _sent.ToList(); } }
    }

    public Task StartAsync(string model, CancellationToken cancellationToken)
    {
        StartedModel = model;
        if (FatalCode is not null)
        {
            Emit(new SpeechCppMessage { Type = "fatal", Error = new SpeechCppError { Code = FatalCode, Message = "load failed" } });
        }
        else if (RespondReady)
        {
            Emit(new SpeechCppMessage { Type = "ready", Protocol = 4, Version = "0.11.0" });
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(SpeechCppMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(message);
        }

        switch (message.Type)
        {
            case "transcribe":
                if (TranscribeErrorCode is not null)
                {
                    Emit(new SpeechCppMessage { Type = "error", Id = message.Id, Error = new SpeechCppError { Code = TranscribeErrorCode, Message = "decode failed" } });
                }
                else
                {
                    Emit(new SpeechCppMessage { Type = "end", Id = message.Id, Text = EndText, Stop = "complete", Segments = EndSegments });
                }

                break;

            case "cancel":
                Emit(new SpeechCppMessage { Type = "cancelled", Id = message.Id });
                break;
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<SpeechCppMessage> ReadMessagesAsync(CancellationToken cancellationToken)
        => _out.Reader.ReadAllAsync(cancellationToken);

    public string DrainStandardError() => string.Empty;

    public void Emit(SpeechCppMessage message) => _out.Writer.TryWrite(message);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        HasExited = true;
        _out.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
