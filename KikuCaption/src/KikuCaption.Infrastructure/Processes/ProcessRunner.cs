using System.ComponentModel;
using System.Diagnostics;

namespace KikuCaption.Infrastructure.Processes;

/// <summary>
/// Result of running a child process. <see cref="Started"/> is false when the
/// executable could not be found on the machine.
/// </summary>
public sealed record ProcessRunResult(bool Started, int ExitCode, string StandardOutput, string StandardError)
{
    public static ProcessRunResult NotFound { get; } = new(false, -1, string.Empty, string.Empty);

    public bool Succeeded => Started && ExitCode == 0;
}

/// <summary>
/// Launches short-lived child processes with a structured argument list (never string
/// concatenation) to avoid command injection (PROJECT.md 13). A missing executable is
/// reported as <see cref="ProcessRunResult.NotFound"/> rather than throwing.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        try
        {
            if (!process.Start())
            {
                return ProcessRunResult.NotFound;
            }
        }
        catch (Win32Exception)
        {
            // Executable not found on PATH.
            return ProcessRunResult.NotFound;
        }
        catch (FileNotFoundException)
        {
            return ProcessRunResult.NotFound;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out (as opposed to caller-requested cancellation).
            TryKill(process);
            return new ProcessRunResult(true, -1, string.Empty, "进程执行超时。");
        }

        var stdout = await SafeRead(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeRead(stderrTask).ConfigureAwait(false);
        return new ProcessRunResult(true, process.ExitCode, stdout, stderr);
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
