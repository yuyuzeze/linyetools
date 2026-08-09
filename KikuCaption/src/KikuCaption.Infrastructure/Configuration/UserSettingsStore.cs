using System.Globalization;
using System.Text.Json;

namespace KikuCaption.Infrastructure.Configuration;

/// <summary>
/// Loads/saves <see cref="UserSettings"/> as JSON in a user-writable directory (Milestone 7 §3).
/// A corrupt file is backed up as <c>settings.corrupt-*.bak</c> and safe defaults are returned
/// (never a silent overwrite). The API key is never read or written here.
/// </summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public UserSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    /// <summary>Default location: <c>%LOCALAPPDATA%/KikuCaption</c> (user-writable).</summary>
    public static UserSettingsStore CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KikuCaption"));

    public string FilePath => _path;

    /// <summary>Loads settings; returns defaults + <c>WasReset=true</c> if missing or corrupt.</summary>
    public (UserSettings Settings, bool WasReset) Load()
    {
        if (!File.Exists(_path))
        {
            return (new UserSettings(), false);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path));
            return loaded is null ? (new UserSettings(), true) : (loaded, false);
        }
        catch (Exception)
        {
            // Corrupt: preserve the bad file for inspection, then fall back to safe defaults.
            try
            {
                var backup = _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak";
                File.Move(_path, backup, overwrite: true);
            }
            catch { /* best effort */ }

            return (new UserSettings(), true);
        }
    }

    public void Save(UserSettings settings)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
