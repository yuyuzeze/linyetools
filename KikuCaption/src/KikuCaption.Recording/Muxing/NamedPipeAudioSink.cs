using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using KikuCaption.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Recording.Muxing;

/// <summary>
/// Thin transport that carries the continuous PCM timeline to FFmpeg over a per-recording,
/// unpredictable, current-user-only named pipe (PROJECT.md 5.3, M5 安全). Buffering/pacing is done
/// by <see cref="AudioTimeline"/>; this type only accepts a connection and writes bytes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeAudioSink : IAsyncDisposable
{
    private readonly ILogger _logger;
    private NamedPipeServerStream? _server;

    public NamedPipeAudioSink(ILogger logger)
    {
        PipeName = "kiku-audio-" + Guid.NewGuid().ToString("N");
        _logger = logger;
    }

    public string PipeName { get; }

    public void CreateServer()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new RecordingException("pipe_acl", "无法确定当前用户 SID 以限制音频管道访问。");
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Out-buffer large enough that the real-time output loop never blocks (FFmpeg reads the
        // pipe slower than real time); the drain-on-close recovers most of the buffered tail.
        _server = NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 1 << 16, security);
    }

    public async Task WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("Pipe server not created.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _server.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RecordingException("pipe_timeout", "FFmpeg 未在超时内连接音频管道。");
        }
    }

    /// <summary>Writes continuous PCM to the pipe. Called only from the timeline output loop.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        if (_server is null || pcm.IsEmpty)
        {
            return;
        }

        await _server.WriteAsync(pcm, cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected => _server?.IsConnected ?? false;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            try { if (_server.IsConnected) _server.Disconnect(); } catch { /* ignore */ }
            await _server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
