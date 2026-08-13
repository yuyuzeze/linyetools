using CommunityToolkit.Mvvm.ComponentModel;
using KikuCaption.App.Localization;
using KikuCaption.Storage.Sqlite;

namespace KikuCaption.App.ViewModels;

/// <summary>Compact, localized projection of one persisted meeting for the home-page history list.</summary>
public sealed class RecentMeetingViewModel : ObservableObject
{
    private readonly LocalizationService _loc;

    public RecentMeetingViewModel(StoredSession stored, LocalizationService localization, bool summaryExists)
    {
        Stored = stored;
        _loc = localization;
        SummaryExists = summaryExists;
    }

    public StoredSession Stored { get; }
    public Guid SessionId => Stored.Session.Id;
    public string Directory => Stored.Session.OutputDirectory;
    public bool SummaryExists { get; }
    public bool HasRecording => !string.IsNullOrWhiteSpace(Stored.Session.RecordingPath);
    public string DateText => Stored.Session.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string LanguageText => _loc["Lang." + Stored.Session.RecognitionLanguage];
    public string DetailText
    {
        get
        {
            var duration = Stored.Session.EndedAt is { } ended
                ? ended - Stored.Session.StartedAt
                : TimeSpan.Zero;
            var durationText = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");
            return string.Format(_loc["History.Detail"], LanguageText, durationText, Stored.SegmentCount);
        }
    }

    public string ArtifactText
    {
        get
        {
            var parts = new List<string>(2);
            if (HasRecording) parts.Add(_loc["History.HasRecording"]);
            if (SummaryExists) parts.Add(_loc["History.HasSummary"]);
            return string.Join(" · ", parts);
        }
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ArtifactText));
    }
}
