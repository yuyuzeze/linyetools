using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Localization;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Core.Enums;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Identity of whatever session the timeline is currently showing (UI-R5C): the live current session
/// or a loaded history session. Both id AND directory come from the actual record — never guessed.
/// </summary>
public sealed record DisplayedSessionInfo(Guid SessionId, string Directory, DateTimeOffset Date, string Language, bool IsLive);

/// <summary>
/// The full-meeting subtitle timeline (Milestone 3.1). Holds <b>every</b> final of the current
/// session — earliest at the top, newest appended at the bottom — and is never trimmed to a
/// "recent N lines" window. Partials never enter the timeline; they only update a single bottom
/// "recognizing" line.
///
/// <para>All auto-scroll <i>decisions</i> live here so they are unit-testable without WPF: the view
/// reports the user's scroll position via <see cref="NotifyAtBottom"/> and reacts to
/// <see cref="ScrollToEndRequested"/>; the actual pixel scrolling and UI virtualization are the
/// view's job (see <c>TimelineAutoScroll</c>). Mutating members must be called on the UI thread —
/// the pipeline events are marshalled there by the caller.</para>
/// </summary>
public partial class MeetingTimelineViewModel : ObservableObject
{
    private readonly ITranscriptStore _store;
    private long _lastSequence;

    /// <summary>Raised when the view should scroll to the newest entry (auto-scroll or jump).</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>True while the view is pinned to the bottom and new finals should auto-scroll.</summary>
    [ObservableProperty]
    private bool _isAutoScroll = true;

    /// <summary>Number of finals that arrived while the user was reading history (scrolled up).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewMessages))]
    [NotifyPropertyChangedFor(nameof(NewMessagesText))]
    private int _newCount;

    /// <summary>The single bottom "recognizing…" line. Provisional; never persisted or archived.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartial))]
    private string _partialText = string.Empty;

    [ObservableProperty]
    private bool _isLoadingHistory;

    [ObservableProperty]
    private string _historyStatus = string.Empty;

    /// <summary>The session the timeline currently shows (live or history), or null when empty (UI-R5C).</summary>
    [ObservableProperty]
    private DisplayedSessionInfo? _displayedSession;

    public MeetingTimelineViewModel(ITranscriptStore store)
    {
        _store = store;
        // Re-localize the "new captions" chip live when the UI language changes (UI-R3). Uses the
        // shared instance so the constructor stays test-friendly (no extra dependency).
        LocalizationService.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(NewMessagesText));
    }

    /// <summary>Every confirmed final, in order. Append-only within a session; never trimmed.</summary>
    public ObservableCollection<CaptionEntryViewModel> Entries { get; } = new();

    public int FinalCount => Entries.Count;

    public bool HasPartial => !string.IsNullOrWhiteSpace(PartialText);

    public bool HasNewMessages => NewCount > 0;

    public string NewMessagesText => NewCount > 0
        ? string.Format(LocalizationService.Instance["Timeline.NewMessages"], NewCount)
        : string.Empty;

    /// <summary>Starts a fresh session timeline (clears the previous one). Called on Start.</summary>
    public void BeginSession()
    {
        Entries.Clear();
        _lastSequence = 0;
        PartialText = string.Empty;
        NewCount = 0;
        IsAutoScroll = true;
        HistoryStatus = string.Empty;
        DisplayedSession = null; // set precisely by SetLiveSession once the session/directory exist
        OnPropertyChanged(nameof(FinalCount));
    }

    /// <summary>
    /// Records the live session's real id + directory once it has been created (UI-R5C). This is the
    /// summary target for the current session — never guessed from "most recent".
    /// </summary>
    public void SetLiveSession(Guid sessionId, string directory, DateTimeOffset date, string language)
        => DisplayedSession = new DisplayedSessionInfo(sessionId, directory, date, language, IsLive: true);

    /// <summary>
    /// Appends a live final produced by the recognition pipeline. The display sequence is the
    /// 1-based arrival order, which matches the SQLite <c>SequenceNumber</c> assigned per final.
    /// </summary>
    public void AppendLive(Guid segmentId, DateTimeOffset createdAt, string text, TranslationDisplayState state = TranslationDisplayState.None)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Entries.Add(new CaptionEntryViewModel(segmentId, ++_lastSequence, createdAt, text) { TranslationState = state });
        PartialText = string.Empty;
        OnPropertyChanged(nameof(FinalCount));

        if (IsAutoScroll)
        {
            // Pinned to bottom: follow the newest line.
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // User is reading earlier history — never yank them down; just count the new lines.
            NewCount++;
        }
    }

    /// <summary>Updates the bottom partial line only. Partials do not create history entries.</summary>
    public void SetPartial(string text) => PartialText = text ?? string.Empty;

    /// <summary>
    /// Updates a caption's translation in place by <paramref name="segmentId"/> (M6). Never adds a
    /// card and never scrolls, so a translation arriving while the user reads history is unobtrusive.
    /// </summary>
    public void ApplyTranslation(Guid segmentId, TranslationDisplayState state, string? translation)
    {
        foreach (var entry in Entries)
        {
            if (entry.SegmentId == segmentId)
            {
                if (translation is not null)
                {
                    entry.Translation = translation;
                }

                entry.TranslationState = state;
                return;
            }
        }
    }

    /// <summary>
    /// Called by the view when the user's scroll position changes. Reaching the bottom resumes
    /// auto-scroll and clears the new-message counter; scrolling up pauses auto-scroll.
    /// </summary>
    public void NotifyAtBottom(bool atBottom)
    {
        IsAutoScroll = atBottom;
        if (atBottom)
        {
            NewCount = 0;
        }
    }

    /// <summary>Jumps to the newest final and resumes auto-scroll (the "N new subtitles" action).</summary>
    [RelayCommand]
    private void JumpToLatest()
    {
        IsAutoScroll = true;
        NewCount = 0;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears only the on-screen timeline. This is a UI action: it never deletes SQLite rows,
    /// exported files, or the MP4. Reloading history brings the finals back.
    /// </summary>
    [RelayCommand]
    private void ClearDisplay()
    {
        Entries.Clear();
        _lastSequence = 0;
        PartialText = string.Empty;
        NewCount = 0;
        IsAutoScroll = true;
        DisplayedSession = null; // nothing shown → no summary target
        HistoryStatus = "已清空显示（数据库与字幕文件未删除）。";
        OnPropertyChanged(nameof(FinalCount));
    }

    /// <summary>
    /// Loads <b>all</b> final subtitles of a session from SQLite, ordered by SequenceNumber, into
    /// the timeline (Milestone 3.1 recovery browse). Partials are excluded. Existing entries are
    /// replaced so the caller sees exactly the persisted history, first to last.
    /// </summary>
    public async Task<int> LoadHistoryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        IsLoadingHistory = true;
        try
        {
            var stored = await _store.GetSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(true);
            LoadHistory(stored);

            // Record the ACTUAL loaded session's id + directory (from SQLite) as the summary target —
            // a history session, so its state is idle regardless of any other running meeting (UI-R5C).
            var session = await _store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(true);
            if (session is not null)
            {
                DisplayedSession = new DisplayedSessionInfo(
                    session.Session.Id, session.Session.OutputDirectory,
                    session.Session.StartedAt, session.Session.RecognitionLanguage, IsLive: false);
            }

            HistoryStatus = $"已从数据库加载 {Entries.Count} 条 final 字幕（会话 {sessionId:N}）。";
            return Entries.Count;
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    /// <summary>
    /// Reopens the most recently started meeting from SQLite and loads its full final history.
    /// Used to browse a session from end to end after a crash/restart (Milestone 3.1).
    /// </summary>
    [RelayCommand]
    private async Task LoadMostRecentSessionAsync()
    {
        IsLoadingHistory = true;
        try
        {
            var session = await _store.GetMostRecentSessionAsync(CancellationToken.None).ConfigureAwait(true);
            if (session is null)
            {
                HistoryStatus = "数据库中没有任何会话记录。";
                return;
            }

            await LoadHistoryAsync(session.Session.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HistoryStatus = "加载历史会话失败：" + ex.Message;
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    /// <summary>Replaces the timeline with the given stored finals, in sequence order (testable).</summary>
    public void LoadHistory(IEnumerable<StoredSegment> stored)
    {
        Entries.Clear();
        _lastSequence = 0;
        foreach (var s in stored
                     .Where(s => s.Segment.Status != TranscriptStatus.Partial)
                     .OrderBy(s => s.SequenceNumber))
        {
            var seg = s.Segment;
            var state = seg.Status switch
            {
                TranscriptStatus.Translated when !string.IsNullOrWhiteSpace(seg.Translation) => TranslationDisplayState.Translated,
                TranscriptStatus.TranslationFailed => TranslationDisplayState.Failed,
                _ => TranslationDisplayState.None
            };
            Entries.Add(new CaptionEntryViewModel(seg.Id, s.SequenceNumber, seg.CreatedAt, seg.Text)
            {
                Translation = seg.Translation,
                TranslationState = state
            });
            _lastSequence = s.SequenceNumber;
        }

        PartialText = string.Empty;
        NewCount = 0;
        IsAutoScroll = true;
        OnPropertyChanged(nameof(FinalCount));
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }
}
