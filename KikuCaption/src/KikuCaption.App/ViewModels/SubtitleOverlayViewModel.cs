using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.Infrastructure.Configuration;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// State for the subtitle overlay (recent lines, current partial, appearance). All caption
/// business state lives here — not in the window code-behind (PROJECT.md M3). Mutating methods
/// must be called on the UI thread (the pipeline events are marshalled there by the caller).
/// </summary>
public partial class SubtitleOverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private double _fontSize = 26;

    [ObservableProperty]
    private double _backgroundOpacity = 0.85;

    [ObservableProperty]
    private int _maxLines = 4;

    [ObservableProperty]
    private bool _clickThrough;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartial))]
    private string _partialText = string.Empty;

    public SubtitleOverlayViewModel(SubtitleSettings settings)
    {
        FontSize = settings.FontSize;
        BackgroundOpacity = settings.Opacity;
        MaxLines = Math.Clamp(settings.MaxLines, 2, 5);
        ClickThrough = settings.ClickThrough;
    }

    public ObservableCollection<CaptionLineViewModel> Lines { get; } = new();

    public bool HasPartial => !string.IsNullOrWhiteSpace(PartialText);

    public void AddFinal(Guid segmentId, string text, bool translating = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Lines.Add(new CaptionLineViewModel(segmentId, text, isFinal: true) { Translating = translating });
        TrimToMaxLines();
        PartialText = string.Empty;
    }

    /// <summary>Fills a line's translation in place by segment id (M6). Ignores trimmed-out lines.</summary>
    public void ApplyTranslation(Guid segmentId, string? translation, bool translating)
    {
        foreach (var line in Lines)
        {
            if (line.SegmentId == segmentId)
            {
                if (translation is not null)
                {
                    line.Translation = translation;
                }

                line.Translating = translating;
                return;
            }
        }
    }

    public void SetPartial(string text) => PartialText = text ?? string.Empty;

    public void Clear()
    {
        Lines.Clear();
        PartialText = string.Empty;
    }

    partial void OnMaxLinesChanged(int value) => TrimToMaxLines();

    private void TrimToMaxLines()
    {
        while (Lines.Count > Math.Max(1, MaxLines))
        {
            Lines.RemoveAt(0);
        }
    }
}
