using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace KikuCaption.Translation.Security;

/// <summary>
/// Stores the translation API key as Windows DPAPI (CurrentUser) ciphertext on disk (M6 §8,
/// PROJECT.md 5.6). Only the encrypted bytes are written; the plaintext key never touches config,
/// Git, logs, SQLite, or session files. A fixed application entropy is mixed in so the ciphertext is
/// only usable by this app for the current Windows user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTranslationSecretStore : ITranslationSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KikuCaption.Translation.ApiKey.v1");

    private readonly string _filePath;

    public DpapiTranslationSecretStore(string secretsDirectory)
    {
        Directory.CreateDirectory(secretsDirectory);
        _filePath = Path.Combine(secretsDirectory, "translation.key");
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%/KikuCaption/secrets</c>.</summary>
    public static DpapiTranslationSecretStore CreateDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KikuCaption", "secrets");
        return new DpapiTranslationSecretStore(dir);
    }

    public bool IsConfigured => File.Exists(_filePath);

    public void Save(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("API key must not be empty.", nameof(secret));
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        // Write to a temp file then move, so a crash never leaves a half-written key file.
        var temp = _filePath + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, _filePath, overwrite: true);
    }

    public string Read()
    {
        if (!File.Exists(_filePath))
        {
            throw new InvalidOperationException("未配置 API Key。");
        }

        var cipher = File.ReadAllBytes(_filePath);
        // Decryption failure (wrong user / corrupt ciphertext) throws CryptographicException; we let
        // it propagate WITHOUT deleting the file, so the user can retry or re-enter the key.
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
