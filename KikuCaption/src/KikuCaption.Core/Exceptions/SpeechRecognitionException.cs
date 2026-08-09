namespace KikuCaption.Core.Exceptions;

/// <summary>
/// Raised when the speech recognizer / worker fails (initialization failure, protocol error,
/// timeout, or unexpected worker exit). Messages are user-safe and never contain PCM data.
/// </summary>
public sealed class SpeechRecognitionException : Exception
{
    public SpeechRecognitionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public SpeechRecognitionException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine-readable error code (e.g. "worker_exited", "timeout").</summary>
    public string Code { get; }
}
