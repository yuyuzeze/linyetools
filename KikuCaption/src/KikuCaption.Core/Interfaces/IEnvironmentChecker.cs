using KikuCaption.Core.Models;

namespace KikuCaption.Core.Interfaces;

/// <summary>
/// Inspects the host machine for the dependencies KikuCaption needs
/// (.NET runtime, Python, FFmpeg, available disk space) and reports the result.
/// The implementation must not throw for missing optional tooling; it reports status instead.
/// </summary>
public interface IEnvironmentChecker
{
    Task<EnvironmentReport> CheckAsync(CancellationToken cancellationToken = default);
}
