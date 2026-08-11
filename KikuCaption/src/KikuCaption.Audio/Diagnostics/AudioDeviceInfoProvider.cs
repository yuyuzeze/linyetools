using NAudio.CoreAudioApi;

namespace KikuCaption.Audio.Diagnostics;

/// <summary>Non-secret facts about the default audio render (output) endpoint.</summary>
public sealed record AudioOutputDeviceInfo(string Name, bool IsActive);

/// <summary>
/// Reports the default audio output (render) endpoint used for WASAPI loopback capture. Kept in the
/// Audio module (the only place that depends on NAudio) and consumed by the environment probe in the
/// App layer, so no UI/probe code needs a direct NAudio dependency.
/// </summary>
public interface IAudioDeviceInfoProvider
{
    /// <summary>The default render endpoint, or null when there is no active output device.</summary>
    AudioOutputDeviceInfo? GetDefaultOutputDevice();
}

/// <inheritdoc />
public sealed class AudioDeviceInfoProvider : IAudioDeviceInfoProvider
{
    public AudioOutputDeviceInfo? GetDefaultOutputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            return null;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new AudioOutputDeviceInfo(device.FriendlyName, device.State == DeviceState.Active);
    }
}
