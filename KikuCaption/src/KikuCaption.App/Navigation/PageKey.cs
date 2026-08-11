namespace KikuCaption.App.Navigation;

/// <summary>
/// Identifies a top-level page hosted inside the main window (UI-R1 §3 in-window navigation).
/// UI-R1 ships Home and Environment as functional pages; Audio, Dictionary and Settings are present
/// in the information architecture but route to a placeholder until UI-R2 / R3 / R4 build them.
/// </summary>
public enum PageKey
{
    Home,
    Environment,
    Audio,
    Dictionary,
    Settings
}
