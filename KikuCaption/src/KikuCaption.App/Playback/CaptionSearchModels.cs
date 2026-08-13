using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.Playback;

public sealed record CaptionSearchSource(
    string TimeText,
    string Text,
    string? Translation,
    object Target);

public sealed class CaptionSearchResult
{
    public CaptionSearchResult(CaptionSearchSource source)
    {
        TimeText = source.TimeText;
        Text = source.Text;
        Translation = source.Translation;
        Target = source.Target;
    }

    public string TimeText { get; }
    public string Text { get; }
    public string? Translation { get; }
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
    public object Target { get; }
}

public sealed partial class CaptionSearchViewModel : ObservableObject
{
    private readonly CaptionSearchSource[] _sources;

    public CaptionSearchViewModel(IEnumerable<CaptionSearchSource> sources)
    {
        _sources = sources.ToArray();
        RefreshResults();
    }

    public ObservableCollection<CaptionSearchResult> Results { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private CaptionSearchResult? _selectedResult;

    public int TotalCount => _sources.Length;
    public int ResultCount => Results.Count;

    partial void OnQueryChanged(string value) => RefreshResults();

    private void RefreshResults()
    {
        var query = Query.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _sources
            : _sources.Where(source =>
                source.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(source.Translation) &&
                 source.Translation.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        Results.Clear();
        foreach (var match in matches) Results.Add(new CaptionSearchResult(match));
        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(ResultCount));
    }
}
