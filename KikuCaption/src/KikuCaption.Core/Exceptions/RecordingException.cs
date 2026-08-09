namespace KikuCaption.Core.Exceptions;

/// <summary>
/// Raised for recording failures (FFmpeg missing/crash, target gone, pipe failure, disk).
/// Messages are user-safe and never contain PCM/subtitle content.
/// </summary>
public sealed class RecordingException : Exception
{
    public RecordingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public RecordingException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
