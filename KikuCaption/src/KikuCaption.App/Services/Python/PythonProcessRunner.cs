using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KikuCaption.App.Services.Python;

/// <summary>Outcome of a python/pip run. <see cref="TimedOut"/>/exit non-zero ⇒ failure.</summary>
public sealed record PythonProcessResult(int ExitCode, string TailOutput, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>Runs one python/pip command. Abstracted so install branches are testable with a fake.</summary>
public interface IPythonProcessRunner
{
    Task<PythonProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine,
        CancellationToken cancellationToken);
}

/// <summary>
/// Real runner (R7C): structured <see cref="ProcessStartInfo.ArgumentList"/> (never string
/// concatenation), stdout+stderr read ASYNCHRONOUSLY (no pipe deadlock), the child assigned to a
/// kill-on-close Job Object (no orphans on cancel/exit), a bounded diagnostic tail, and URL query /
/// user-info scrubbed from that tail so a token in a package-index URL never surfaces or is logged.
/// </summary>
public sealed partial class PythonProcessRunner : IPythonProcessRunner
{
    private const int MaxTailLines = 200;

    public async Task<PythonProcessResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, IProgress<string>? onOutputLine, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var job = OperatingSystem.IsWindows() ? new ProcessJobObject() : null;
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var tail = new Queue<string>(MaxTailLines);
        void Capture(string? line)
        {
            if (line is null) return;
            var scrubbed = Scrub(line);
            lock (tail)
            {
                if (tail.Count >= MaxTailLines) tail.Dequeue();
                tail.Enqueue(scrubbed);
            }
            onOutputLine?.Report(scrubbed);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        if (!process.Start())
        {
            return new PythonProcessResult(-1, "failed to start", false);
        }

        job?.TryAssign(process); // kill-on-close covers the whole tree
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string tailText;
        lock (tail) { tailText = string.Join("\n", tail); }
        return new PythonProcessResult(process.ExitCode, tailText, false);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    // Reduce any URL in a log line to scheme://host/path — drops query (?sig=…) and user:pass@.
    private static string Scrub(string line) => UrlInLine().Replace(line, m =>
    {
        return Uri.TryCreate(m.Value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : "(redacted-url)";
    });

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInLine();
}
