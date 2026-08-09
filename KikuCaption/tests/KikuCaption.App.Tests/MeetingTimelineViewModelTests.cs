using System.IO;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// Milestone 3.1 acceptance: the full-meeting timeline keeps every final (first→last), never
/// trims, never lets a partial into history, and its auto-scroll decisions behave correctly.
/// The store-backed tests use a real SQLite database in a temp directory.
/// </summary>
public sealed class MeetingTimelineViewModelTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly SqliteTranscriptRepository _repo;

    public MeetingTimelineViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kiku_app_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "kikucaption.db");
        _repo = new SqliteTranscriptRepository(_dbPath, NullLogger<SqliteTranscriptRepository>.Instance);
    }

    private MeetingTimelineViewModel NewTimeline() => new(_repo);

    private static IReadOnlyList<StoredSegment> SyntheticFinals(int count, Guid sessionId)
    {
        // Build the epoch at the local UTC offset so the wall-clock card time is timezone-stable.
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 9, 10, 0, 0));
        var epoch = new DateTimeOffset(2026, 8, 9, 10, 0, 0, localOffset);
        var list = new List<StoredSegment>(count);
        for (int i = 1; i <= count; i++)
        {
            var seg = new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                StartTime = TimeSpan.FromSeconds(i),
                EndTime = TimeSpan.FromSeconds(i + 1),
                Language = "ja",
                Text = $"第{i}文の内容です。",
                Status = TranscriptStatus.Final,
                CreatedAt = epoch.AddSeconds(i)
            };
            list.Add(new StoredSegment(seg, i));
        }

        return list;
    }

    // Tests 1/2/3: 5000 finals — first→last present, endpoints correct, no loss/duplication.
    [Fact]
    public void Load5000_FirstToLast_Ordered_NoLossNoDuplicate()
    {
        var vm = NewTimeline();
        var sessionId = Guid.NewGuid();

        vm.LoadHistory(SyntheticFinals(5000, sessionId));

        Assert.Equal(5000, vm.Entries.Count);
        Assert.Equal(5000, vm.FinalCount);

        // First and last content, time, sequence.
        Assert.Equal(1, vm.Entries[0].SequenceNumber);
        Assert.Equal("第1文の内容です。", vm.Entries[0].Text);
        Assert.Equal("10:00:01", vm.Entries[0].Time);
        Assert.Equal(5000, vm.Entries[^1].SequenceNumber);
        Assert.Equal("第5000文の内容です。", vm.Entries[^1].Text);

        // Middle: sequence numbers are contiguous 1..5000 with no gaps or duplicates.
        var seqs = vm.Entries.Select(e => e.SequenceNumber).ToArray();
        Assert.Equal(Enumerable.Range(1, 5000).Select(i => (long)i), seqs);
        Assert.Equal(5000, seqs.Distinct().Count());
    }

    // Test 10: partials update only the bottom line and never enter the history.
    [Fact]
    public void Partial_NeverEntersHistory()
    {
        var vm = NewTimeline();
        vm.BeginSession();

        // Many partials arrive — none create a history entry, only the bottom line updates.
        for (int i = 0; i < 50; i++)
        {
            vm.SetPartial($"認識中 {i}");
        }
        Assert.Empty(vm.Entries);
        Assert.Equal("認識中 49", vm.PartialText);

        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "確定した文1"); // a final clears the partial line
        Assert.Single(vm.Entries);
        Assert.Equal(string.Empty, vm.PartialText);

        vm.SetPartial("認識中 next"); // partial shows again but is still not history
        Assert.Single(vm.Entries);
        Assert.Equal("認識中 next", vm.PartialText);

        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "確定した文2");
        Assert.Equal(2, vm.Entries.Count); // only the two finals ever entered history
        Assert.All(vm.Entries, e => Assert.DoesNotContain("認識中", e.Text));
        Assert.Equal(string.Empty, vm.PartialText);
    }

    // Auto-scroll req 1: at bottom, a new final follows to the newest line and never counts as "new".
    [Fact]
    public void AtBottom_NewFinal_AutoScrolls_NoNewCount()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        int scrollRequests = 0;
        vm.ScrollToEndRequested += (_, _) => scrollRequests++;

        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "一つ目");
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "二つ目");

        Assert.True(vm.IsAutoScroll);
        Assert.Equal(0, vm.NewCount);
        Assert.Equal(2, scrollRequests);
    }

    // Tests 6/7: scrolled up, new finals do NOT force the view down; the new-count is correct.
    [Fact]
    public void ScrolledUp_NewFinals_DoNotForceBottom_CountCorrect()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        int scrollRequests = 0;
        vm.ScrollToEndRequested += (_, _) => scrollRequests++;

        vm.NotifyAtBottom(false); // user scrolled up to read history
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新1");
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新2");
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新3");

        Assert.False(vm.IsAutoScroll);
        Assert.Equal(3, vm.NewCount);
        Assert.True(vm.HasNewMessages);
        Assert.Equal("有 3 条新字幕 ↓", vm.NewMessagesText);
        Assert.Equal(0, scrollRequests); // never yanked to the bottom
        Assert.Equal(3, vm.Entries.Count); // but all finals are retained
    }

    // Test 8: clicking the hint jumps to the newest final and resumes auto-scroll.
    [Fact]
    public void JumpToLatest_ResetsCount_ResumesAutoScroll_Scrolls()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        vm.NotifyAtBottom(false);
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新1");
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新2");
        int scrollRequests = 0;
        vm.ScrollToEndRequested += (_, _) => scrollRequests++;

        vm.JumpToLatestCommand.Execute(null);

        Assert.True(vm.IsAutoScroll);
        Assert.Equal(0, vm.NewCount);
        Assert.False(vm.HasNewMessages);
        Assert.Equal(1, scrollRequests);
    }

    // Auto-scroll req 7: scrolling back to the bottom on your own resumes auto-scroll.
    [Fact]
    public void ScrollBackToBottom_ResumesAutoScroll()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        vm.NotifyAtBottom(false);
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "新1");
        Assert.False(vm.IsAutoScroll);
        Assert.Equal(1, vm.NewCount);

        vm.NotifyAtBottom(true); // user scrolled back down

        Assert.True(vm.IsAutoScroll);
        Assert.Equal(0, vm.NewCount);
    }

    // Test 11: clearing the display is UI-only — SQLite rows survive and can be reloaded.
    [Fact]
    public async Task ClearDisplay_DoesNotDeleteStorage()
    {
        var session = await SeedSessionAsync(120);
        var vm = NewTimeline();
        await vm.LoadHistoryAsync(session, CancellationToken.None);
        Assert.Equal(120, vm.Entries.Count);

        vm.ClearDisplayCommand.Execute(null);
        Assert.Empty(vm.Entries);

        // Storage untouched: the finals are still there and reload fully.
        var stillStored = await _repo.GetSegmentsAsync(session, CancellationToken.None);
        Assert.Equal(120, stillStored.Count);
        await vm.LoadHistoryAsync(session, CancellationToken.None);
        Assert.Equal(120, vm.Entries.Count);
    }

    // Test 9 + requirement 8: recover from SQLite by SequenceNumber — first→last, 5000 rows.
    [Fact]
    public async Task RecoverFromSqlite_5000_FirstToLast_Ordered()
    {
        var session = await SeedSessionAsync(5000);
        var vm = NewTimeline();

        int loaded = await vm.LoadHistoryAsync(session, CancellationToken.None);

        Assert.Equal(5000, loaded);
        Assert.Equal(5000, vm.Entries.Count);
        Assert.Equal(1, vm.Entries[0].SequenceNumber);
        Assert.Equal("第1句", vm.Entries[0].Text);
        Assert.Equal(5000, vm.Entries[^1].SequenceNumber);
        Assert.Equal("第5000句", vm.Entries[^1].Text);
        Assert.Equal(Enumerable.Range(1, 5000).Select(i => (long)i),
            vm.Entries.Select(e => e.SequenceNumber));
    }

    // Defensive: even if a partial were in the row set, the loader excludes it from history.
    [Fact]
    public void LoadHistory_ExcludesPartials()
    {
        var vm = NewTimeline();
        var sessionId = Guid.NewGuid();
        var epoch = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        TranscriptSegment Seg(int seq, TranscriptStatus status) => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            StartTime = TimeSpan.FromSeconds(seq),
            EndTime = TimeSpan.FromSeconds(seq + 1),
            Language = "ja",
            Text = status == TranscriptStatus.Final ? $"final{seq}" : $"partial{seq}",
            Status = status,
            CreatedAt = epoch.AddSeconds(seq)
        };

        vm.LoadHistory(new[]
        {
            new StoredSegment(Seg(1, TranscriptStatus.Final), 1),
            new StoredSegment(Seg(2, TranscriptStatus.Partial), 2),
            new StoredSegment(Seg(3, TranscriptStatus.Final), 3),
        });

        Assert.Equal(2, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.StartsWith("final", e.Text));
    }

    private async Task<Guid> SeedSessionAsync(int finals)
    {
        var sessionId = Guid.NewGuid();
        var session = new MeetingSession
        {
            Id = sessionId,
            StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = "zh",
            OutputDirectory = _root
        };
        await _repo.CreateSessionAsync(session, CancellationToken.None);
        for (int i = 1; i <= finals; i++)
        {
            await _repo.UpsertSegmentAsync(new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                StartTime = TimeSpan.FromSeconds(i),
                EndTime = TimeSpan.FromSeconds(i + 1),
                Language = "zh",
                Text = $"第{i}句",
                Status = TranscriptStatus.Final,
                CreatedAt = DateTimeOffset.Now
            }, CancellationToken.None);
        }

        return sessionId;
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
