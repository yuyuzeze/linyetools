using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Records the screen (or a window) plus system audio to an MP4 via a managed FFmpeg subprocess
/// (PROJECT.md 5.3, 8.2). V1 does not implement a video encoder in .NET.
/// </summary>
public interface IScreenRecorder : IAsyncDisposable
{
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);

    Task<RecordingResult> StopAsync(CancellationToken cancellationToken);

    RecorderState State { get; }

    /// <summary>PID of the live FFmpeg subprocess, or null if not running (Milestone 7 resource监控).</summary>
    int? RecordingProcessId { get; }
}
