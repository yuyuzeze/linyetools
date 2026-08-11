using KikuCaption.Core.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Infrastructure.Diagnostics;
using KikuCaption.Infrastructure.Processes;
using Microsoft.Extensions.Options;
using Xunit;

namespace KikuCaption.Infrastructure.Tests;

/// <summary>
/// UI-R1 §6: the environment FFmpeg probe resolves the exe through the shared <see cref="FFmpegResolver"/>
/// (so it agrees with recording), surfaces the resolved path, and verifies the exe actually runs.
/// </summary>
public class FFmpegProbeTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;
        public string? LastFileName { get; private set; }
        public FakeProcessRunner(ProcessRunResult result) => _result = result;

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            return Task.FromResult(_result);
        }
    }

    private static IOptions<KikuCaptionOptions> Options(string? ffmpegPath)
        => Microsoft.Extensions.Options.Options.Create(new KikuCaptionOptions
        {
            Recording = new RecordingSettings { FFmpegPath = ffmpegPath }
        });

    [Fact] // resolved ffmpeg runs → Ok, version + resolved path surfaced, and the SAME path recording would use
    public async Task RunnableFFmpeg_ReportsOk_WithResolvedPath()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_probe_ok");
        try
        {
            var ffmpeg = Path.Combine(dir.FullName, "ffmpeg.exe");
            File.WriteAllText(ffmpeg, "stub");
            File.WriteAllText(Path.Combine(dir.FullName, "ffprobe.exe"), "stub");

            var runner = new FakeProcessRunner(new ProcessRunResult(true, 0, "ffmpeg version 6.1.1 built with gcc", string.Empty));
            var probe = new FFmpegProbe(runner, Options(ffmpeg));

            var result = await probe.ProbeAsync(CancellationToken.None);

            Assert.Equal(EnvironmentCheckStatus.Ok, result.Status);
            Assert.Equal(Path.GetFullPath(ffmpeg), result.ResolvedPath);
            Assert.Contains("6.1.1", result.DetectedVersion);
            // Ran the resolved full path, not the bare "ffmpeg" name → same result as recording's locator.
            Assert.Equal(Path.GetFullPath(ffmpeg), runner.LastFileName);
            Assert.Equal(FFmpegResolver.Resolve(ffmpeg, dir.FullName).FFmpegPath, result.ResolvedPath);
        }
        finally { dir.Delete(true); }
    }

    [Fact] // file exists but cannot launch → Error (not a false "OK")
    public async Task FoundButNotRunnable_ReportsError()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_probe_broken");
        try
        {
            var ffmpeg = Path.Combine(dir.FullName, "ffmpeg.exe");
            File.WriteAllText(ffmpeg, "not a real exe");

            var runner = new FakeProcessRunner(ProcessRunResult.NotFound);
            var probe = new FFmpegProbe(runner, Options(ffmpeg));

            var result = await probe.ProbeAsync(CancellationToken.None);

            Assert.Equal(EnvironmentCheckStatus.Error, result.Status);
            Assert.Equal(Path.GetFullPath(ffmpeg), result.ResolvedPath);
        }
        finally { dir.Delete(true); }
    }

    [Fact] // nothing found → Missing but NON-blocking (recording-only; captions still run)
    public async Task NotFound_IsMissingButNotRequired()
    {
        var dir = Directory.CreateTempSubdirectory("kiku_probe_missing");
        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            var probe = new FFmpegProbe(new FakeProcessRunner(ProcessRunResult.NotFound), Options(null));

            // The probe resolves from AppContext.BaseDirectory; whatever it finds, FFmpeg is
            // recording-only and must never be a blocking (red) dependency — captions still run.
            var result = await probe.ProbeAsync(CancellationToken.None);

            Assert.False(result.IsRequired); // FFmpeg is never blocking (yellow, not red)
        }
        finally { Environment.SetEnvironmentVariable("PATH", original); dir.Delete(true); }
    }
}
