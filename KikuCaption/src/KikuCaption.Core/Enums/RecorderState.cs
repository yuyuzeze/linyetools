namespace KikuCaption.Core.Enums;

/// <summary>Lifecycle state of the screen recorder (PROJECT.md 8.2).</summary>
public enum RecorderState
{
    Idle,
    Starting,
    Recording,
    Stopping,
    Stopped,
    Faulted
}
