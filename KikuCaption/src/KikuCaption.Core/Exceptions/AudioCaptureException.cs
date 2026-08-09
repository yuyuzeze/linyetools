namespace KikuCaption.Core.Exceptions;

/// <summary>
/// Raised when system audio capture fails or is interrupted (device disconnect, format
/// change, WASAPI error). Messages are safe to show in the UI and never contain PCM data.
/// </summary>
public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(string message)
        : base(message)
    {
    }

    public AudioCaptureException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
