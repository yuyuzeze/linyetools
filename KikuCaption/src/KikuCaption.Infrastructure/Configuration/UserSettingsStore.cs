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
    private readonly string _editionMarkerDirectory;

    /// <param name="directory">User-writable settings directory.</param>
    /// <param name="editionMarkerDirectory">Directory holding the packaged <c>edition.txt</c> marker;
    /// defaults to the app base directory (next to the executable). Injectable for tests.</param>
    public UserSettingsStore(string directory, string? editionMarkerDirectory = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _editionMarkerDirectory = editionMarkerDirectory ?? AppContext.BaseDirectory;
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
            // First launch: the packaged edition decides the speech-recognition default. The Full
            // edition (and any dev build) defaults ON; the RecordingOnly edition defaults OFF because
            // it ships without Python/Whisper. The user can flip it in Settings and upgrade later.
            return (new UserSettings { EnableSpeechRecognition = FirstLaunchSpeechDefault() }, false);
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

    // Reads the optional edition marker (edition.txt) shipped next to the executable. Only the
    // RecordingOnly edition turns the first-launch speech default OFF; anything else keeps it ON.
    private bool FirstLaunchSpeechDefault()
    {
        try
        {
            var marker = Path.Combine(_editionMarkerDirectory, "edition.txt");
            if (File.Exists(marker) &&
                File.ReadAllText(marker).Trim().Equals("RecordingOnly", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch { /* best effort — fall back to the ON default */ }

        return true;
    }

    public void Save(UserSettings settings)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
