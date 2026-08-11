using System.Runtime.InteropServices;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;

namespace KikuCaption.Infrastructure.Diagnostics;

/// <summary>
/// Reports the .NET runtime the app is currently executing on. Because the app itself
/// is built for .NET 10, a successful launch already implies a compatible runtime; this
/// probe surfaces the exact version for diagnostics.
/// </summary>
public sealed class DotNetRuntimeProbe : IEnvironmentProbe
{
    public DependencyKind Kind => DependencyKind.DotNetRuntime;
    public string DisplayName => ".NET 运行时";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var version = Environment.Version;
        var description = RuntimeInformation.FrameworkDescription;
        var isNet10OrLater = version.Major >= 10;

        var result = new DependencyCheckResult
        {
            Kind = Kind,
            Name = DisplayName,
            IsRequired = true,
            Status = isNet10OrLater ? EnvironmentCheckStatus.Ok : EnvironmentCheckStatus.Warning,
            DetectedVersion = description,
            MessageCode = isNet10OrLater ? "EnvMsg.DotNet.Ok" : "EnvMsg.DotNet.Old",
            RemediationCode = isNet10OrLater ? null : "EnvRem.DotNet.Old"
        };

        return Task.FromResult(result);
    }
}
