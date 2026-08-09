using System.IO.Pipes;
using KikuCaption.Core.Exceptions;
using KikuCaption.Recording.Muxing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Recording.Tests;

public class NamedPipeAudioSinkTests
{
    [Fact] // connect, write, client reads bytes
    public async Task Connect_Write_ClientReadsBytes()
    {
        await using var sink = new NamedPipeAudioSink(NullLogger.Instance);
        sink.CreateServer();
        using var client = new NamedPipeClientStream(".", sink.PipeName, PipeDirection.In, PipeOptions.Asynchronous);

        var connect = client.ConnectAsync(5000);
        await sink.WaitForConnectionAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await connect;

        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        var buffer = new byte[payload.Length];
        var readTask = Task.Run(async () =>
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await client.ReadAsync(buffer.AsMemory(total));
                if (n == 0) break;
                total += n;
            }

            return total;
        });

        await sink.WriteAsync(payload, CancellationToken.None);
        int read = await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buffer);
        Assert.True(sink.IsConnected);
    }

    [Fact] // connect timeout
    public async Task WaitForConnection_Timeout_Throws()
    {
        await using var sink = new NamedPipeAudioSink(NullLogger.Instance);
        sink.CreateServer();
        var ex = await Assert.ThrowsAsync<RecordingException>(
            () => sink.WaitForConnectionAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
        Assert.Equal("pipe_timeout", ex.Code);
    }

    [Fact] // external cancel
    public async Task WaitForConnection_Cancelled_Throws()
    {
        await using var sink = new NamedPipeAudioSink(NullLogger.Instance);
        sink.CreateServer();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.WaitForConnectionAsync(TimeSpan.FromSeconds(5), cts.Token));
    }
}
