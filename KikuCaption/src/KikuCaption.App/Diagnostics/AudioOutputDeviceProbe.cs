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
                Detail = "无法枚举音频输出设备。",
                Remediation = "请检查 Windows 声音设置中的输出设备。"
            });
        }

        var result = device is null
            ? new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Missing,
                Detail = "未检测到默认音频输出设备，无法采集系统声音。",
                Remediation = "请在 Windows 声音设置中启用一个输出设备后重新检查。"
            }
            : new DependencyCheckResult
            {
                Kind = Kind,
                Name = DisplayName,
                IsRequired = true,
                Status = EnvironmentCheckStatus.Ok,
                DetectedVersion = device.Name,
                Detail = "已检测到默认音频输出设备。"
            };

        return Task.FromResult(result);
    }
}
