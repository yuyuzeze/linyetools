using System.ComponentModel;
using System.Windows.Data;

namespace KikuCaption.App.Localization;

/// <summary>
/// Central localization service (UI-R3). Holds the current UI culture and resolves keys through the
/// bilingual <see cref="LocalizedStrings"/> tables. XAML binds to the indexer via the <c>Loc</c>
/// markup extension; changing the language raises an indexer change so every bound string refreshes
/// live. View models depend on this service instead of scattering zh/en literals.
///
/// A missing key falls back to the zh-CN table, then to the key itself — never throws. Caption text,
/// meeting files and logs are never routed through here.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>
    /// Shared instance for the <c>Loc</c> markup extension (which cannot use DI). The same instance
    /// is registered in the container, so bindings and view models see one source of truth.
    /// </summary>
    public static LocalizationService Instance { get; } = new();

    private string _currentLanguage = LocalizedStrings.ZhCN;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes (for code that must react beyond data binding).</summary>
    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    /// <summary>Resolves a key in the current culture, with safe fallback.</summary>
    public string this[string key] => Resolve(key, _currentLanguage);

    public string Get(string key) => Resolve(key, _currentLanguage);

    /// <summary>Switches the UI language. No-op for an unknown or unchanged culture.</summary>
    public void SetLanguage(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)
            || !LocalizedStrings.Tables.ContainsKey(culture)
            || string.Equals(culture, _currentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentLanguage = culture;
        // Binding.IndexerName ("Item[]") refreshes every {Binding [Key]} against this source.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Resolve(string key, string culture)
    {
        if (key is null)
        {
            return string.Empty;
        }

        if (LocalizedStrings.Tables.TryGetValue(culture, out var table) && table.TryGetValue(key, out var value))
        {
            return value;
        }

        // Fallback: zh-CN, then the key itself (visible but never a crash).
        if (LocalizedStrings.Tables[LocalizedStrings.ZhCN].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }
}
