using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace KikuCaption.Translation.Security;

/// <summary>
/// Stores translation secrets (the API key and, optionally, an endpoint URL that embeds a credential)
/// as Windows DPAPI (CurrentUser) ciphertext on disk (M6 §8, PROJECT.md 5.6). Only encrypted bytes
/// are written; plaintext never touches config, Git, logs, SQLite, or session files. A fixed
/// application entropy is mixed in so the ciphertext is only usable by this app for the current user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTranslationSecretStore : ITranslationSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KikuCaption.Translation.ApiKey.v1");

    private readonly string _keyPath;
    private readonly string _endpointPath;

    public DpapiTranslationSecretStore(string secretsDirectory)
    {
        Directory.CreateDirectory(secretsDirectory);
        _keyPath = Path.Combine(secretsDirectory, "translation.key");
        _endpointPath = Path.Combine(secretsDirectory, "translation.endpoint");
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%/KikuCaption/secrets</c>.</summary>
    public static DpapiTranslationSecretStore CreateDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KikuCaption", "secrets");
        return new DpapiTranslationSecretStore(dir);
    }

    // ----- API key -----

    public bool IsConfigured => File.Exists(_keyPath);

    public void Save(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("API key must not be empty.", nameof(secret));
        }

        WriteEncrypted(_keyPath, secret);
    }

    public string Read()
    {
        if (!File.Exists(_keyPath))
        {
            throw new InvalidOperationException("未配置 API Key。");
        }

        return ReadEncrypted(_keyPath);
    }

    public void Delete() => DeleteIfExists(_keyPath);

    // ----- Endpoint (may embed a credential, e.g. a function key in the path) -----

    public bool HasEndpoint => File.Exists(_endpointPath);

    public void SaveEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Endpoint must not be empty.", nameof(endpoint));
        }

        WriteEncrypted(_endpointPath, endpoint);
    }

    public string? ReadEndpoint() => File.Exists(_endpointPath) ? ReadEncrypted(_endpointPath) : null;

    public void DeleteEndpoint() => DeleteIfExists(_endpointPath);

    // ----- helpers -----

    private static void WriteEncrypted(string path, string value)
    {
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        // Write to a temp file then move, so a crash never leaves a half-written file.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, path, overwrite: true);
    }

    private static string ReadEncrypted(string path)
    {
        var cipher = File.ReadAllBytes(path);
        // Decryption failure (wrong user / corrupt ciphertext) throws CryptographicException; we let
        // it propagate WITHOUT deleting the file, so the user can retry or re-enter the value.
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
