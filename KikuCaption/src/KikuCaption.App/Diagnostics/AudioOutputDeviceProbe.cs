using KikuCaption.Audio.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Diagnostics;

/// <summary>
/// Verifies there is a default audio output (render) endpoint — WASAPI loopback captures the
/// system's output, so without an active output device there is nothing to caption. Required (red)
/// when absent. Device enumeration failures are treated as a warning rather than crashing the check.
/// </summary>
public sealed class AudioOutputDeviceProbe : IEnvironmentProbe
{
    private readonly IAudioDeviceInfoProvider _devices;
    private readonly ILogger<AudioOutputDeviceProbe> _logger;

    public AudioOutputDeviceProbe(IAudioDeviceInfoProvider devices, ILogger<AudioOutputDeviceProbe> logger)
    {
        _devices = devices;
        _logger = logger;
    }

    public DependencyKind Kind => DependencyKind.AudioOutputDevice;
    public string DisplayName => "音频输出设备";

    public Task<DependencyCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        AudioOutputDeviceInfo? device;
        try
        {
            device = _devices.GetDefaultOutputDevice();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio output device enumeration failed.");
            return Task.FromResult(new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = false,
                Status = EnvironmentCheckStatus.Warning,
                MessageCode = "EnvMsg.Audio.EnumFail",
                RemediationCode = "EnvRem.Audio.EnumFail"
            });
        }

        var result = device is null
            ? new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                MessageCode = "EnvMsg.Audio.Missing",
                RemediationCode = "EnvRem.Audio.Missing"
            }
            : new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                DetectedVersion = device.Name,
                MessageCode = "EnvMsg.Audio.Ok"
            };

        return Task.FromResult(result);
    }
}
