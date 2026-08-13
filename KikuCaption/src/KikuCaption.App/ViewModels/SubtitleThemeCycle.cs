namespace KikuCaption.App.ViewModels;

public static class SubtitleThemeCycle
{
    public static string Next(string? current) => current switch
    {
        "night-sakura" => "deep-sea",
        "deep-sea" => "default",
        _ => "night-sakura"
    };
}
