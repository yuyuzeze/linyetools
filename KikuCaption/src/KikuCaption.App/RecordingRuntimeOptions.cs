namespace KikuCaption.App;

/// <summary>Resolved recording settings for the UI: located FFmpeg path + encoder preferences.</summary>
public sealed record RecordingRuntimeOptions(
    string? FFmpegPath,
    int FrameRate,
    string PreferredEncoder,
    string FallbackEncoder);
