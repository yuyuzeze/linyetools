using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using KikuCaption.Core.Exceptions;
using KikuCaption.Speech.Protocol;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.SpeechCpp;

/// <summary>
/// Real speech.cpp transport: launches <c>speech.exe worker MODEL --device … [--threads …]</c>,
/// speaks JSON Lines over stdin/stdout and reads stderr on a separate loop. A kill-on-close Job
/// Object plus an explicit kill-tree on dispose prevent orphan processes — mirrors
/// <see cref="ProcessWhisperWorker"/>, but for the speech.cpp message schema.
/// </summary>
public sealed class ProcessSpeechCppWorker : ISpeechCppWorker
{
    private const int MaxStderrLines = 200;

    private readonly SpeechCppWorkerOptions _options;
    private readonly ILogger<ProcessSpeechCppWorker> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrLines = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private Channel<SpeechCppMessage>? _incoming;
    private WindowsJobObject? _job;
    private Task? _stderrLoop;
    private Task? _readLoop;

    public ProcessSpeechCppWorker(SpeechCppWorkerOptions options, ILogger<ProcessSpeechCppWorker> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool HasExited
    {
        get
        {
            try { return _process?.HasExited ?? true; }
            catch { return true; }
        }
    }

    public int? ExitCode
    {
        get
        {
            try { return _process is { HasExited: true } p ? p.ExitCode : null; }
            catch { return null; }
        }
    }

    public Task StartAsync(string model, CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("speech.cpp worker 已启动。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new SpeechRecognitionException("model_missing", "未指定 speech.cpp 模型。");
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            throw new SpeechRecognitionException("speech_exe_missing",
                $"找不到 speech.exe：{_options.ExecutablePath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add(model);
        if (!string.IsNullOrWhiteSpace(_options.Device))
        {
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(_options.Device);
        }

        if (_options.Threads is int threads and > 0)
        {
            startInfo.ArgumentList.Add("--threads");
            startInfo.ArgumentList.Add(threads.ToString());
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new SpeechRecognitionException("worker_start_failed", "无法启动 speech.cpp worker 进程。");
        }

        _process = process;

        try
        {
            _job = new WindowsJobObject();
            _job.Assign(process);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job Object 不可用，将仅依赖显式 kill 进行清理。");
            _job = null;
        }

        _incoming = Channel.CreateBounded<SpeechCppMessage>(new BoundedChannelOptions(_options.IncomingCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _readLoop = Task.Run(() => ReadLoopAsync(process.StandardOutput, _cts.Token));
        _stderrLoop = Task.Run(() => ReadStderrAsync(process.StandardError, _cts.Token));

        _logger.LogInformation("speech.cpp worker started (pid {Pid}, model {Model}).", process.Id, model);
        return Task.CompletedTask;
    }

    public async Task SendAsync(SpeechCppMessage message, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("speech.cpp worker 尚未启动。");
        var line = SpeechCppCodec.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IAsyncEnumerable<SpeechCppMessage> ReadMessagesAsync(CancellationToken cancellationToken)
    {
        if (_incoming is null)
        {
            throw new InvalidOperationException("speech.cpp worker 尚未启动。");
        }

        return _incoming.Reader.ReadAllAsync(cancellationToken);
    }

    public string DrainStandardError()
    {
        lock (_stderrGate)
        {
            return string.Join(Environment.NewLine, _stderrLines);
        }
    }

    private async Task ReadLoopAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var incoming = _incoming!;
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                SpeechCppMessage message;
                try
                {
                    message = SpeechCppCodec.Parse(line);
                }
                catch (ProtocolException ex)
                {
                    // A non-JSON stdout line is a worker defect; log and skip rather than crash.
                    _logger.LogWarning("Ignoring malformed speech.cpp stdout line: {Code}", ex.Code);
                    continue;
                }

                await incoming.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }

            incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            incoming.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            incoming.Writer.TryComplete(ex);
        }
    }

    private async Task ReadStderrAsync(TextReader reader, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                lock (_stderrGate)
                {
                    _stderrLines.Enqueue(line);
                    while (_stderrLines.Count > MaxStderrLines)
                    {
                        _stderrLines.Dequeue();
                    }
                }

                _logger.LogDebug("speech.cpp stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "speech.cpp stderr reader ended.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { process.StandardInput.Close(); } catch { /* ignore */ }

                    using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* fall through to kill */ }

                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during speech.cpp worker shutdown.");
            }
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        if (_stderrLoop is not null)
        {
            try { await _stderrLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _job?.Dispose();
        try { process?.Dispose(); } catch { /* ignore */ }
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
