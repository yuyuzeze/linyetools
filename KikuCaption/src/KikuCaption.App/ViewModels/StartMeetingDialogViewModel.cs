using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.Recording.CaptureTargets;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Draft view model for the "start meeting" dialog (UI-R2 dialog-draft fix). It is fully
/// independent of the live <see cref="RealtimeCaptionViewModel"/>: it copies the current capture
/// target in as initial values, and all edits stay in this draft. Nothing is written back to the
/// main view model here — the caller applies the draft once, on confirm, via
/// <see cref="MeetingStartCoordinator"/>. Cancel / Esc / close therefore change nothing.
/// </summary>
public sealed partial class StartMeetingDialogViewModel : ObservableObject
{
    public StartMeetingDialogViewModel(MeetingCaptureTarget initial, IEnumerable<string> windows, string outputRoot)
    {
        _captureType = initial.CaptureType;
        _selectedWindow = initial.WindowTitle;
        OutputRoot = outputRoot;

        foreach (var window in windows)
        {
            Windows.Add(window);
        }

        // If seeded with a window that is no longer listed, keep it visible so the choice is retained.
        if (!string.IsNullOrWhiteSpace(_selectedWindow) && !Windows.Contains(_selectedWindow))
        {
            Windows.Insert(0, _selectedWindow!);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowCapture))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string _captureType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private string? _selectedWindow;

    public ObservableCollection<string> Windows { get; } = new();

    public string OutputRoot { get; }

    public bool IsWindowCapture => string.Equals(CaptureType, MeetingCaptureTarget.Window, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the current draft is a valid, startable target.</summary>
    public bool CanStart => ToTarget().IsValid;

    /// <summary>Snapshots the draft into an immutable target (window title dropped for screen capture).</summary>
    public MeetingCaptureTarget ToTarget()
        => new(CaptureType, IsWindowCapture ? SelectedWindow : null);

    [RelayCommand]
    private void RefreshWindows()
    {
        var previous = SelectedWindow;
        Windows.Clear();
        foreach (var window in WindowEnumerator.EnumerateWindows())
        {
            Windows.Add(window.Title);
        }

        if (!string.IsNullOrWhiteSpace(previous) && !Windows.Contains(previous))
        {
            Windows.Insert(0, previous!);
        }
        SelectedWindow = previous;
    }
}
