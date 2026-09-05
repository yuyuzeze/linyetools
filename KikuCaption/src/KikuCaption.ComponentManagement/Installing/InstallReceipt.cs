using System.Text.Json;
using KikuCaption.Core.Models;

namespace KikuCaption.ComponentManagement.Installing;

/// <summary>
/// A non-sensitive install receipt written into a managed component's directory as
/// <c>.component.json</c> (R7B.1). It records what was installed and how far it was verified, so the
/// status page reads the actual installed version + verification level instead of guessing from the
/// folder name. It contains NO remote URL/query, credentials, or machine-sensitive paths.
/// </summary>
public sealed record InstallReceipt(
    int SchemaVersion,
    string ComponentId,
    string Version,
    string Sha256,
    string InstalledAtUtc,
    ModelVerificationLevel VerificationLevel);

/// <summary>Atomic reader/writer for <c>.component.json</c>.</summary>
public static class InstallReceiptStore
{
    public const string FileName = ".component.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string PathFor(string componentDirectory) => Path.Combine(componentDirectory, FileName);

    /// <summary>Writes the receipt atomically (temp file + move) into the component directory.</summary>
    public static void Write(string componentDirectory, InstallReceipt receipt)
    {
        Directory.CreateDirectory(componentDirectory);
        var finalPath = PathFor(componentDirectory);
        var temp = finalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(receipt, Json));
        File.Move(temp, finalPath, overwrite: true);
    }

    /// <summary>Reads the receipt, or null when it is missing or malformed (caller re-verifies).</summary>
    public static InstallReceipt? TryRead(string componentDirectory)
    {
        try
        {
            var path = PathFor(componentDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(path), Json);
            return receipt is { SchemaVersion: CurrentSchemaVersion } ? receipt : null;
        }
        catch
        {
            return null;
        }
    }
}
