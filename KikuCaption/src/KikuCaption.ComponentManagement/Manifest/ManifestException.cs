namespace KikuCaption.ComponentManagement.Manifest;

/// <summary>
/// Raised when a manifest is malformed, has an unsupported schema, references an unknown component
/// type, or contains an unsafe URL / install path. Carries a stable <see cref="Code"/> so the UI can
/// localize the message without parsing free text. The message is deliberately non-sensitive.
/// </summary>
public sealed class ManifestException : Exception
{
    public ManifestException(string code, string message) : base(message) => Code = code;

    /// <summary>Stable machine-readable reason, e.g. "schema", "component-type", "url", "path".</summary>
    public string Code { get; }
}
