namespace KikuCaption.App.Services;

/// <summary>
/// Resolves the meeting-summary output language (UI-R5C §setting). A persisted user choice (zh/ja/en)
/// wins; otherwise — or for an invalid stored value — it follows the current UI culture
/// (zh-CN→zh, ja-JP→ja, en-US→en, anything else→zh). Pure and testable.
/// </summary>
public static class SummaryLanguage
{
    public static readonly IReadOnlyList<string> Supported = new[] { "zh", "ja", "en" };

    /// <summary>The effective output language, given the (nullable) stored choice and the UI culture.</summary>
    public static string Resolve(string? stored, string uiLanguage)
        => IsValid(stored) ? stored! : FromUi(uiLanguage);

    /// <summary>True when the user has explicitly chosen a valid summary language (not following the UI).</summary>
    public static bool HasUserChoice(string? stored) => IsValid(stored);

    /// <summary>Maps a UI culture to a summary language code.</summary>
    public static string FromUi(string uiLanguage) => uiLanguage switch
    {
        "ja-JP" => "ja",
        "en-US" => "en",
        _ => "zh"
    };

    private static bool IsValid(string? value) => value is "zh" or "ja" or "en";
}
