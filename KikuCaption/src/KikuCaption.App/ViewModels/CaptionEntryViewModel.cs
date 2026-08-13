using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KikuCaption.App.ViewModels;

/// <summary>How a caption's translation should be shown (M6 §9).</summary>
public enum TranslationDisplayState
{
    /// <summary>No translation involved (e.g. Chinese recognition mode) — no translation row.</summary>
    None,

    /// <summary>Queued/in-flight — show a small "翻译中" hint.</summary>
    Translating,

    /// <summary>Translated — show the Chinese line under the original.</summary>
    Translated,

    /// <summary>Failed — keep the original, show a low-key "翻译失败" marker.</summary>
    Failed
}

/// <summary>
/// One <b>final</b> subtitle in the full-meeting timeline (M3.1) with its translation lifecycle
/// (M6). The original text/time/sequence are immutable; only the translation and its display state
/// change — in place — so the same card is updated when the translation returns (never a new card).
/// </summary>
public sealed partial class CaptionEntryViewModel : ObservableObject
{
    public CaptionEntryViewModel(Guid segmentId, long sequenceNumber, DateTimeOffset createdAt, string text,
        TimeSpan? startTime = null, TimeSpan? endTime = null)
    {
        SegmentId = segmentId;
        SequenceNumber = sequenceNumber;
        CreatedAt = createdAt;
        Text = text;
        StartTime = startTime ?? TimeSpan.Zero;
        EndTime = endTime ?? StartTime;
        Time = createdAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Join key with storage and the translation queue.</summary>
    public Guid SegmentId { get; }

    public long SequenceNumber { get; }
    public DateTimeOffset CreatedAt { get; }
    public string Time { get; }
    public string Text { get; }
    public TimeSpan StartTime { get; }
    public TimeSpan EndTime { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string? _translation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslating))]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private TranslationDisplayState _translationState;

    public bool HasTranslation => TranslationState == TranslationDisplayState.Translated && !string.IsNullOrWhiteSpace(Translation);

    public bool IsTranslating => TranslationState == TranslationDisplayState.Translating;

    public bool HasFailed => TranslationState == TranslationDisplayState.Failed;
}
