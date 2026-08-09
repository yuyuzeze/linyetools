namespace KikuCaption.Speech.Protocol;

/// <summary>A malformed or invalid protocol message (bad JSON, version, fields, PCM, size).</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
