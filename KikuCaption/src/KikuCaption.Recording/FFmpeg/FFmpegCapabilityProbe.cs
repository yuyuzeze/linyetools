using KikuCaption.Recording.Processes;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Recording.FFmpeg;

public sealed record FFmpegCapabilities(string Version, bool HasQuickSync);

/// <summary>
/// Real capability probe (PROJECT.md 14.2, M5): reads the FFmpeg version and attempts a short
/// actual <c>h264_qsv</c> encode. Quick Sync availability is decided by that real encode, not by
/// the encoder list. Failures are logged (sanitized) and fall back to libx264.
/// </summary>
public sealed class FFmpegCapabilityProbe
{
    private readonly ILogger<FFmpegCapabilityProbe> _logger;

    public FFmpegCapabilityProbe(ILogger<FFmpegCapabilityProbe> logger) => _logger = logger;

    public async Task<FFmpegCapabilities> ProbeAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var version = await ReadVersionAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        var hasQsv = await TryQuickSyncEncodeAsync(ffmpegPath, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("FFmpeg capability probe: version='{Version}', QuickSync={Qsv}.", version, hasQsv);
        return new FFmpegCapabilities(version, hasQsv);
    }

    private static async Task<string> ReadVersionAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffmpegPath, new[] { "-hide_banner", "-version" },
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var firstLine = result.StandardOutput.Split('\n').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(firstLine) ? "unknown" : firstLine;
    }

    private async Task<bool> TryQuickSyncEncodeAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        // Encode 0.2 s of a synthetic source with h264_qsv to null — a genuine QSV encode attempt.
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=320x240:r=15",
            "-t", "0.2", "-c:v", "h264_qsv", "-f", "null", "-"
        };

        try
        {
            var result = await ProcessRunner.RunAsync(ffmpegPath, args, TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode == 0 && !result.TimedOut)
            {
                return true;
            }

            var reason = result.TimedOut ? "timeout" : $"exit={result.ExitCode}";
            _logger.LogWarning("Quick Sync probe failed ({Reason}); falling back to libx264.", reason);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick Sync probe error; falling back to libx264.");
            return false;
        }
    }
}
