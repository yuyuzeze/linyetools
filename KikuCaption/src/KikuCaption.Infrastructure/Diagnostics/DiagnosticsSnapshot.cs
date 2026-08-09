using System.Globalization;
using System.Text;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// A periodic, non-sensitive resource/perf snapshot (Milestone 7 §5). Contains only numbers — no
/// caption text, translation text, PCM, window titles, or keys — so it is safe to log.
/// </summary>
public sealed record DiagnosticsSnapshot
{
    public double? MainCpuPercent { get; init; }
    public double? WorkerCpuPercent { get; init; }
    public double? FfmpegCpuPercent { get; init; }

    public long? MainWorkingSet { get; init; }
    public long? WorkerWorkingSet { get; init; }
    public long? FfmpegWorkingSet { get; init; }

    public double? Rtf { get; init; }
    public long? LastInferenceMs { get; init; }
    public int AudioQueueDepthMs { get; init; }
    public long DroppedAudioChunks { get; init; }
    public int TranslationQueueDepth { get; init; }
    public double FreeDiskGb { get; init; }
    public long Mp4Bytes { get; init; }
    public double Mp4GrowthKbPerSec { get; init; }

    public double? TotalCpuPercent =>
        (MainCpuPercent ?? 0) + (WorkerCpuPercent ?? 0) + (FfmpegCpuPercent ?? 0) is var t && HasAnyCpu ? t : null;

    private bool HasAnyCpu => MainCpuPercent is not null || WorkerCpuPercent is not null || FfmpegCpuPercent is not null;

    public long? TotalWorkingSet =>
        MainWorkingSet is null && WorkerWorkingSet is null && FfmpegWorkingSet is null
            ? null
            : (MainWorkingSet ?? 0) + (WorkerWorkingSet ?? 0) + (FfmpegWorkingSet ?? 0);
}

/// <summary>Formats a <see cref="DiagnosticsSnapshot"/> as a single redacted log/UI line.</summary>
public static class DiagnosticsFormatter
{
    public static string ToLogLine(DiagnosticsSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("perf cpu[main=").Append(Pct(s.MainCpuPercent))
          .Append(" worker=").Append(Pct(s.WorkerCpuPercent))
          .Append(" ffmpeg=").Append(Pct(s.FfmpegCpuPercent))
          .Append(" total=").Append(Pct(s.TotalCpuPercent)).Append(']');
        sb.Append(" mem[total=").Append(Mb(s.TotalWorkingSet)).Append(']');
        sb.Append(" rtf=").Append(s.Rtf?.ToString("0.00", CultureInfo.InvariantCulture) ?? "n/a");
        sb.Append(" inf=").Append(s.LastInferenceMs?.ToString(CultureInfo.InvariantCulture) ?? "n/a").Append("ms");
        sb.Append(" audioQ=").Append(s.AudioQueueDepthMs).Append("ms drop=").Append(s.DroppedAudioChunks);
        sb.Append(" transQ=").Append(s.TranslationQueueDepth);
        sb.Append(" disk=").Append(s.FreeDiskGb.ToString("0.0", CultureInfo.InvariantCulture)).Append("GB");
        sb.Append(" mp4=").Append(Mb(s.Mp4Bytes)).Append('(')
          .Append(s.Mp4GrowthKbPerSec.ToString("0.0", CultureInfo.InvariantCulture)).Append("KB/s)");
        return sb.ToString();
    }

    /// <summary>UI health chip: OK / WARN / high based on total CPU and free disk.</summary>
    public static string HealthLabel(DiagnosticsSnapshot s, double minDiskGb)
    {
        if (s.FreeDiskGb < minDiskGb)
        {
            return "磁盘不足";
        }

        var cpu = s.TotalCpuPercent ?? 0;
        return cpu >= 90 ? "CPU 偏高" : "运行正常";
    }

    private static string Pct(double? v) => v is null ? "n/a" : v.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Mb(long? bytes) => bytes is null ? "n/a" : (bytes.Value / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + "MB";
}
