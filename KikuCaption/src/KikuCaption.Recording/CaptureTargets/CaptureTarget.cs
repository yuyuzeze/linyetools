namespace KikuCaption.Recording.CaptureTargets;

/// <summary>A selectable top-level window (title shown to the user; handle kept for re-validation).</summary>
public sealed record CaptureTarget(nint Handle, string Title);
