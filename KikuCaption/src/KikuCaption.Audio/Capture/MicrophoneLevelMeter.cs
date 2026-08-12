using System.Runtime.Versioning;
using KikuCaption.Audio.Conversion;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KikuCaption.Audio.Capture;

/// <summary>
/// Lightweight live input-level monitor for the start-meeting dialog (UI-R5A "输入音量"). Opens the
/// chosen microphone, exposes a normalized 0..1 RMS level (polled by the UI on a timer — no cross-
/// thread event marshalling), and reports whether the device is usable. It never blocks the WASAPI
/// thread and holds no history; it is fully independent of the session mixer. A device error leaves
/// the level at 0 and <see cref="IsAvailable"/> false rather than throwing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneLevelMeter : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private AudioFormatConverter? _converter;
    private double _level;
    private bool _available;

    /// <summary>Most recent RMS level in [0, 1]. Decays toward 0 when the input is quiet.</summary>
    public double CurrentLevel { get { lock (_gate) { return _level; } } }

    /// <summary>True once capture is running on a real device.</summary>
    public bool IsAvailable { get { lock (_gate) { return _available; } } }

    /// <summary>Starts metering the given input device (null = default communications). Safe to call
    /// after <see cref="Stop"/>; returns false and stays unavailable if the device cannot be opened.</summary>
    public bool Start(string? deviceId)
    {
        Stop();
        try
        {
            var device = ResolveDevice(deviceId);
            var capture = new WasapiCapture(device);
            var converter = new AudioFormatConverter(capture.WaveFormat);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                try
                {
                    var pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
                    UpdateLevel(pcm);
                }
                catch { /* metering is best-effort */ }
            };

            capture.RecordingStopped += (_, _) =>
            {
                lock (_gate) { _available = false; _level = 0; }
            };

            capture.StartRecording();
            lock (_gate)
            {
                _device = device;
                _capture = capture;
                _converter = converter;
                _available = true;
            }
            return true;
        }
        catch
        {
            lock (_gate) { _available = false; _level = 0; }
            return false;
        }
    }

    private void UpdateLevel(byte[] pcm)
    {
        if (pcm.Length < 2)
        {
            return;
        }

        long sumSquares = 0;
        int count = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sumSquares += (long)s * s;
        }

        double rms = Math.Sqrt(sumSquares / (double)count) / short.MaxValue;
        lock (_gate)
        {
            // Fast attack, slow release so a short syllable is visible but the bar does not flicker.
            _level = rms > _level ? rms : (_level * 0.8) + (rms * 0.2);
        }
    }

    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_gate)
        {
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
            _converter = null;
            _available = false;
            _level = 0;
        }

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { /* ignore */ }
            try { capture.Dispose(); } catch { /* ignore */ }
        }

        device?.Dispose();
    }

    private static MMDevice ResolveDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (string.Equals(d.ID, deviceId, StringComparison.Ordinal))
                    {
                        return d;
                    }

                    d.Dispose();
                }
            }

            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    public void Dispose() => Stop();
}
