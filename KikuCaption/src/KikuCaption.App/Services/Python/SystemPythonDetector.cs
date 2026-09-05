using System.IO;
using System.Text.Json;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services.Python;

/// <summary>Discovers + probes candidate system Python interpreters (R7C). Interface for testability.</summary>
public interface ISystemPythonDetector
{
    Task<IReadOnlyList<PythonCandidate>> DetectAsync(string? configuredPython, CancellationToken cancellationToken);
}

/// <summary>
/// Finds compatible system Python interpreters (R7C). It does NOT trust the first "python" on PATH:
/// it gathers candidates from configuration, the <c>py</c> launcher, PATH, and common per-user install
/// locations, then runs a STRUCTURED probe on each (real version, x64, venv module). A Windows Store
/// alias fails the probe and is discarded safely. Versions are judged against
/// <see cref="PythonPaths"/> — never guessed from a filename. Only the version is surfaced; full paths
/// are not logged.
/// </summary>
public sealed class SystemPythonDetector : ISystemPythonDetector
{
    // Prints a one-line JSON fact sheet; a Store alias / broken interpreter produces no valid JSON.
    private const string ProbeScript =
        "import json,sys,importlib.util;print(json.dumps({'ma':sys.version_info[0],'mi':sys.version_info[1],'mc':sys.version_info[2],'x64':sys.maxsize>2**32,'venv':importlib.util.find_spec('venv') is not None}))";

    private readonly IPythonProcessRunner _runner;
    private readonly ILogger<SystemPythonDetector> _logger;

    public SystemPythonDetector(IPythonProcessRunner runner, ILogger<SystemPythonDetector> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>Returns all probed candidates (compatible + incompatible), preferred first.</summary>
    public async Task<IReadOnlyList<PythonCandidate>> DetectAsync(string? configuredPython, CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // path -> priority
        void Add(string? path, int priority)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (!byPath.TryGetValue(full, out var existing) || priority < existing) byPath[full] = priority;
        }

        Add(configuredPython, 0);
        foreach (var v in new[] { "-3.13", "-3.12" }) Add(await ResolveViaAsync("py", new[] { v, "-c", "import sys;print(sys.executable)" }, cancellationToken), 1);
        foreach (var p in await WhereAsync("python.exe", cancellationToken)) Add(p, 2);
        foreach (var p in await WhereAsync("python3.exe", cancellationToken)) Add(p, 2);
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        foreach (var minor in new[] { PythonPaths.MaxMinor, PythonPaths.MinMinor })
            Add(Path.Combine(programs, $"Python{PythonPaths.RequiredMajor}{minor}", "python.exe"), 3);

        var results = new List<PythonCandidate>();
        foreach (var (path, priority) in byPath)
        {
            var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            results.Add(new PythonCandidate(path, priority, info));
        }

        // Compatible first, then by priority, then by version (newest micro).
        return results
            .OrderByDescending(c => c.Info?.Compatible == true)
            .ThenBy(c => c.Priority)
            .ToList();
    }

    /// <summary>The best compatible candidate, or null.</summary>
    public static PythonCandidate? Best(IReadOnlyList<PythonCandidate> candidates)
        => candidates.FirstOrDefault(c => c.Info?.Compatible == true);

    private async Task<PythonProbeInfo?> ProbeAsync(string python, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var lastLine = new LastLine();
            var result = await _runner.RunAsync(python, new[] { "-c", ProbeScript }, lastLine, timeout.Token).ConfigureAwait(false);
            if (!result.Succeeded || lastLine.Value is null) return null;

            using var doc = JsonDocument.Parse(lastLine.Value);
            var r = doc.RootElement;
            int major = r.GetProperty("ma").GetInt32(), minor = r.GetProperty("mi").GetInt32(), micro = r.GetProperty("mc").GetInt32();
            bool x64 = r.GetProperty("x64").GetBoolean(), venv = r.GetProperty("venv").GetBoolean();
            bool compatible = x64 && venv && PythonPaths.IsSupportedVersion(major, minor);
            return new PythonProbeInfo($"{major}.{minor}.{micro}", x64, venv, compatible);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // probe timed out (e.g. a Store alias that hangs) → treated as unusable
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Python probe failed for a candidate.");
            return null;
        }
    }

    private async Task<string?> ResolveViaAsync(string launcher, string[] args, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var last = new LastLine();
            var r = await _runner.RunAsync(launcher, args, last, timeout.Token).ConfigureAwait(false);
            return r.Succeeded ? last.Value?.Trim() : null;
        }
        catch { return null; }
    }

    private async Task<IReadOnlyList<string>> WhereAsync(string name, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var lines = new LineCollector();
            var r = await _runner.RunAsync("where", new[] { name }, lines, timeout.Token).ConfigureAwait(false);
            return r.ExitCode == 0 ? lines.Lines : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private sealed class LastLine : IProgress<string>
    {
        public string? Value { get; private set; }
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) Value = value; }
    }

    private sealed class LineCollector : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) Lines.Add(value.Trim()); }
    }
}
