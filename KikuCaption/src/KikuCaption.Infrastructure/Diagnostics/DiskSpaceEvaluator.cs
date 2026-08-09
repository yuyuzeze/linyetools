using KikuCaption.Core.Enums;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Pure disk-space evaluation logic, separated from the OS query so it can be unit tested.
/// </summary>
public static class DiskSpaceEvaluator
{
    public const double BytesPerGb = 1024d * 1024d * 1024d;

    public static double ToGigabytes(long bytes) => bytes / BytesPerGb;

    /// <summary>
    /// Returns <see cref="EnvironmentCheckStatus.Warning"/> when free space is below the
    /// configured minimum, otherwise <see cref="EnvironmentCheckStatus.Ok"/>.
    /// </summary>
    public static EnvironmentCheckStatus Evaluate(long freeBytes, double minimumGb)
    {
        var freeGb = ToGigabytes(freeBytes);
        return freeGb < minimumGb
            ? EnvironmentCheckStatus.Warning
            : EnvironmentCheckStatus.Ok;
    }
}
