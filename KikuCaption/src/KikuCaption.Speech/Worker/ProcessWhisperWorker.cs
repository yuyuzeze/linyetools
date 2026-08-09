using System.Diagnostics;
using System.Text;
using KikuCaption.Core.Exceptions;
using KikuCaption.Speech.Protocol;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Speech.Worker;

/// <summary>
/// Real worker transport: launches the venv Python process running <c>main.py</c>, wires
/// stdin/stdout via <see cref="JsonLinesChannel"/> and reads stderr on a separate loop.
/// Orphan processes are prevented with a kill-on-close Job Object plus an explicit
/// kill-tree fallback on dispose.
/// </summary>
public sealed class ProcessWhisperWorker : IWhisperWorker
{
    private const int MaxStderrLines = 200;

    private readonly WhisperWorkerOptions _options;
    private readonly ILogger<ProcessWhisperWorker> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stderrGate = new();
    private readonly Queue<string> _stderrLines = new();

    private Process? _process;
    private JsonLinesChannel? _channel;
    private WindowsJobObject? _job;
    private Task? _stderrLoop;

    public ProcessWhisperWorker(WhisperWorkerOptions options, ILogger<ProcessWhisperWorker> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process?.HasExited ?? true;
            }
            catch
            {
                // Process handle released (disposed) — treat as exited.
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process is { HasExited: true } process ? process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Worker 已启动。");
        }

        if (!File.Exists(_options.PythonExecutable))
        {
            throw new SpeechRecognitionException("python_missing",
                $"找不到 Python 可执行文件：{_options.PythonExecutable}");
        }

        if (!File.Exists(_options.WorkerScript))
        {
            throw new SpeechRecognitionException("worker_missing",
                $"找不到 Worker 脚本：{_options.WorkerScript}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerScript) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add(_options.WorkerScript);
        if (!string.IsNullOrWhiteSpace(_options.ModelCacheDirectory))
        {
            startInfo.ArgumentList.Add("--download-root");
            startInfo.ArgumentList.Add(_options.ModelCacheDirectory!);
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new SpeechRecognitionException("worker_start_failed", "无法启动 Worker 进程。");
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

        _channel = new JsonLinesChannel(process.StandardOutput, process.StandardInput,
            _options.IncomingCapacity, _logger);
        _stderrLoop = Task.Run(() => ReadStderrAsync(process.StandardError, _cts.Token));

        _logger.LogInformation("Whisper worker started (pid {Pid}).", process.Id);
        return Task.CompletedTask;
    }

    public Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("Worker 尚未启动。");
        }

        return _channel.SendAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<ProtocolMessage> ReadMessagesAsync(CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("Worker 尚未启动。");
        }

        return _channel.ReadMessagesAsync(cancellationToken);
    }

    public string DrainStandardError()
    {
        lock (_stderrGate)
        {
            return string.Join(Environment.NewLine, _stderrLines);
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

                _logger.LogDebug("worker stderr: {Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "stderr reader ended.");
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
                    // Signal EOF on stdin so a well-behaved worker exits, then wait briefly.
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
                _logger.LogWarning(ex, "Error during worker shutdown.");
            }
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_stderrLoop is not null)
        {
            try { await _stderrLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        // Kill-on-close backstop for any lingering child.
        _job?.Dispose();
        try { process?.Dispose(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
