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
}
