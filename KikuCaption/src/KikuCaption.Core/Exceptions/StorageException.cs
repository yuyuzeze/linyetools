namespace KikuCaption.Core.Exceptions;

/// <summary>
/// Raised for storage failures (disk space, database init/upgrade, path validation, write
/// errors). Messages are user-safe and never contain transcript text.
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public StorageException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
