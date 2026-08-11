using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;

namespace KikuCaption.Speech.Stabilization;

/// <summary>
/// Stable-prefix stabilizer using a LocalAgreement policy over recent candidates (PROJECT.md 9).
///
/// It compares the last N recognizer candidates (whitespace-insensitive, per Unicode rune so it
/// works on space-free Japanese/Chinese) and commits their longest common prefix. The committed
/// prefix is monotonic within an utterance — it never retracts when a later candidate rewrites or
/// backtracks the unstable tail. <see cref="Flush"/> emits the pending text as an immutable final
/// segment and resets for the next utterance.
/// </summary>
public sealed class TranscriptStabilizer : ITranscriptStabilizer
{
    private readonly ProgressiveCaptionOptions _options;
    private readonly Guid _sessionId;
    private readonly string _language;
    private readonly LinkedList<string> _recent = new();

    private string _committed = string.Empty;
    private string _lastPartial = string.Empty;
    private TimeSpan _start;
    private TimeSpan _end;
    private bool _hasStart;

    public TranscriptStabilizer(ProgressiveCaptionOptions options, Guid sessionId, string language)
    {
        _options = options;
        _sessionId = sessionId;
        _language = language;
    }

    public StabilizationResult Process(TranscriptUpdate update)
    {
        var text = (update.Text ?? string.Empty).Trim();

        if (!_hasStart)
        {
            _start = update.StartTime;
            _hasStart = true;
        }

        _end = update.EndTime;

        // Empty candidate: don't disturb committed/partial state.
        if (text.Length == 0)
        {
            _recent.AddLast(string.Empty);
            TrimRecent();
            return new StabilizationResult
            {
                StableText = _committed,
                PartialText = _lastPartial,
                StableAdvanced = false,
                StartTime = _start,
                EndTime = _end
            };
        }

        _recent.AddLast(text);
        TrimRecent();

        // Only trust agreement once we have at least two candidates.
        var candidates = _recent.Where(c => c.Length > 0).ToList();
        int commonCount = candidates.Count >= 2 ? CaptionText.CommonSignificantPrefixCount(candidates) : 0;
        string commonText = CaptionText.TakeSignificantPrefix(text, commonCount);

        bool advanced = false;
        if (CaptionText.SignificantStartsWith(commonText, _committed) &&
            CaptionText.SignificantCount(commonText) > CaptionText.SignificantCount(_committed))
        {
            _committed = commonText;
            advanced = true;
        }

        // Display: keep the growing latest text, but never show less than the committed prefix.
        _lastPartial = CaptionText.SignificantStartsWith(text, _committed) ? text : _committed;

        return new StabilizationResult
        {
            StableText = _committed,
            PartialText = _lastPartial,
            StableAdvanced = advanced,
            StartTime = _start,
            EndTime = _end
        };
    }

    public IReadOnlyList<TranscriptSegment> Flush(TimeSpan endTime)
    {
        // Data-loss Hotfix: prefer the FULLEST known text (_lastPartial), not the conservative
        // stable-agreed prefix (_committed). _lastPartial is always >= _committed in content — it
        // equals the latest full candidate whenever that candidate still starts with _committed,
        // and only falls back to _committed if the latest candidate genuinely contradicts it. A hard
        // finalize (max length/wait/flush) can fire before a still-growing tail (e.g. one more word
        // recognized this very cycle) has had a second cycle to reconfirm it via LocalAgreement —
        // using _committed there would silently drop that already-transcribed tail even though its
        // audio was correctly consumed. Content must never be sacrificed for stability here.
        var text = (_lastPartial.Length > 0 ? _lastPartial : _committed).Trim();
        var segments = new List<TranscriptSegment>();

        if (text.Length > 0)
        {
            segments.Add(new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = _sessionId,
                StartTime = _hasStart ? _start : TimeSpan.Zero,
                EndTime = endTime,
                Language = _language,
                Text = text,
                Status = TranscriptStatus.Final,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        Reset();
        return segments;
    }

    private void Reset()
    {
        _recent.Clear();
        _committed = string.Empty;
        _lastPartial = string.Empty;
        _start = default;
        _end = default;
        _hasStart = false;
    }

    private void TrimRecent()
    {
        while (_recent.Count > _options.RecentCandidates)
        {
            _recent.RemoveFirst();
        }
    }
}
