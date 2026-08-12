using NAudio.CoreAudioApi;

namespace KikuCaption.Audio.Diagnostics;

/// <summary>Non-secret facts about the default audio render (output) endpoint.</summary>
public sealed record AudioOutputDeviceInfo(string Name, bool IsActive);

/// <summary>A selectable microphone (input) endpoint: stable id + friendly name (UI-R5A).</summary>
public sealed record AudioCaptureDeviceInfo(string Id, string Name, bool IsDefaultCommunications);

/// <summary>
/// Reports audio endpoints. Kept in the Audio module (the only place that depends on NAudio) and
/// consumed by the environment probe / start dialog in the App layer, so no UI/probe code needs a
/// direct NAudio dependency. Enumeration failures degrade gracefully (empty list / null), never crash.
/// </summary>
public interface IAudioDeviceInfoProvider
{
    /// <summary>The default render endpoint, or null when there is no active output device.</summary>
    AudioOutputDeviceInfo? GetDefaultOutputDevice();

    /// <summary>Active microphone (input) endpoints with stable ids, for device selection.</summary>
    IReadOnlyList<AudioCaptureDeviceInfo> GetCaptureDevices();

    /// <summary>The stable id of the default communications input device, or null if none.</summary>
    string? GetDefaultCommunicationsCaptureDeviceId();
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

    public IReadOnlyList<AudioCaptureDeviceInfo> GetCaptureDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultCommsId = enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                ? WithDefaultComms(enumerator)
                : null;

            var result = new List<AudioCaptureDeviceInfo>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                result.Add(new AudioCaptureDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultCommsId));
                d.Dispose();
            }

            return result;
        }
        catch
        {
            return Array.Empty<AudioCaptureDeviceInfo>(); // enumeration failure must not crash the app
        }
    }

    public string? GetDefaultCommunicationsCaptureDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                ? WithDefaultComms(enumerator)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string WithDefaultComms(MMDeviceEnumerator enumerator)
    {
        using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        return d.ID;
    }
}
