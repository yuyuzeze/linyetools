using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// One subtitle line in the overlay: original text plus an optional translation that is filled in
/// place when the JA→ZH translation returns (M6). The translation is bound two-line under the
/// original; it never replaces the original and never creates a second line entry.
/// </summary>
public sealed partial class CaptionLineViewModel : ObservableObject
{
    public CaptionLineViewModel(Guid segmentId, string original, bool isFinal)
    {
        SegmentId = segmentId;
        Original = original;
        IsFinal = isFinal;
    }

    public Guid SegmentId { get; }

    public string Original { get; }

    public bool IsFinal { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string? _translation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslating))]
    private bool _translating;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    public bool IsTranslating => Translating && !HasTranslation;
}
