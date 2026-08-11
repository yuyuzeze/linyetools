namespace KikuCaption.Translation.Security;

/// <summary>
/// Local, per-user secure storage for the translation API key (M6 §8). Only ciphertext is written
/// to disk; the plaintext key is never persisted to config, Git, logs, SQLite, or session files.
/// </summary>
public interface ITranslationSecretStore
{
    /// <summary>True when a ciphertext key file exists (does not attempt to decrypt).</summary>
    bool IsConfigured { get; }

    /// <summary>Encrypts and saves the key, replacing any existing one.</summary>
    void Save(string secret);

    /// <summary>
    /// Decrypts and returns the stored key. Throws if not configured or if decryption fails; the
    /// ciphertext is left intact so the user can retry or re-enter (never silently deleted).
    /// </summary>
    string Read();

    /// <summary>Permanently removes the stored ciphertext (user-initiated "clear key").</summary>
    void Delete();

    // The Endpoint URL is stored encrypted too, because some company gateways embed the credential
    // in the URL itself (e.g. a function key in the path). It is never written to plaintext config.

    /// <summary>True when an encrypted endpoint is stored.</summary>
    bool HasEndpoint { get; }

    /// <summary>Encrypts and saves the endpoint URL, replacing any existing one.</summary>
    void SaveEndpoint(string endpoint);

    /// <summary>Decrypts and returns the stored endpoint, or null if none is stored.</summary>
    string? ReadEndpoint();

    /// <summary>Removes the stored endpoint ciphertext.</summary>
    void DeleteEndpoint();
}
