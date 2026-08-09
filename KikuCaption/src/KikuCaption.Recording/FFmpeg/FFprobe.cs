using System.Globalization;
using System.Text.Json;
using KikuCaption.Recording.Processes;

namespace KikuCaption.Recording.FFmpeg;

public sealed record FfprobeResult(
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    int Width,
    int Height,
    double? FrameRate,
    TimeSpan? VideoDuration,
    TimeSpan? AudioDuration,
    long SizeBytes,
    double? VideoStartTime = null,
    double? AudioStartTime = null)
{
    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;
    public bool IsPlayable => Container is not null && HasVideo && SizeBytes > 0;
}

/// <summary>Runs ffprobe to report the real container/codecs/resolution/durations of an MP4.</summary>
public static class FFprobe
{
    public static async Task<FfprobeResult?> ProbeAsync(string ffprobePath, string filePath, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", filePath
        };

        var result = await ProcessRunner.RunAsync(ffprobePath, args, TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var root = doc.RootElement;

            string? container = null;
            long size = 0;
            if (root.TryGetProperty("format", out var format))
            {
                container = format.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
                if (format.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var s))
                {
                    size = s;
                }
            }

            string? videoCodec = null, audioCodec = null;
            int width = 0, height = 0;
            double? frameRate = null;
            TimeSpan? videoDuration = null, audioDuration = null;
            double? videoStart = null, audioStart = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    var duration = ParseDuration(stream);

                    if (type == "video" && videoCodec is null)
                    {
                        videoCodec = codec;
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        frameRate = ParseFrameRate(stream);
                        videoDuration = duration;
                        videoStart = ParseStartTime(stream);
                    }
                    else if (type == "audio" && audioCodec is null)
                    {
                        audioCodec = codec;
                        audioDuration = duration;
                        audioStart = ParseStartTime(stream);
                    }
                }
            }

            if (size == 0)
            {
                try { size = new FileInfo(filePath).Length; } catch { /* ignore */ }
            }

            return new FfprobeResult(container, videoCodec, audioCodec, width, height, frameRate,
                videoDuration, audioDuration, size, videoStart, audioStart);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ParseFrameRate(JsonElement stream)
    {
        if (!stream.TryGetProperty("r_frame_rate", out var rf))
        {
            return null;
        }

        var value = rf.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den != 0)
        {
            return num / den;
        }

        return null;
    }

    private static double? ParseStartTime(JsonElement stream)
    {
        if (stream.TryGetProperty("start_time", out var s) &&
            double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        return null;
    }

    private static TimeSpan? ParseDuration(JsonElement stream)
    {
        if (stream.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
