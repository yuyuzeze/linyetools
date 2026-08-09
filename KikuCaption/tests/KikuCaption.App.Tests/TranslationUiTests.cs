using System.IO;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>M6 UI: translations update the matching card in place — no new card, no forced scroll,
/// double-line overlay, Chinese-mode has no translation area, recovery restores translations.</summary>
public sealed class TranslationUiTests : IAsyncDisposable
{
    private readonly string _root;
    private readonly SqliteTranscriptRepository _repo;

    public TranslationUiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kiku_tr_ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _repo = new SqliteTranscriptRepository(Path.Combine(_root, "k.db"), NullLogger<SqliteTranscriptRepository>.Instance);
    }

    private MeetingTimelineViewModel NewTimeline() => new(_repo);

    // UI 1/3: translation updates the same card in place and does NOT scroll.
    [Fact]
    public void ApplyTranslation_UpdatesInPlace_NoNewCard_NoScroll()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        var id = Guid.NewGuid();
        vm.AppendLive(id, DateTimeOffset.Now, "今回のリリースについて確認します。", TranslationDisplayState.Translating);
        vm.NotifyAtBottom(false); // user reading history
        int scrolls = 0;
        vm.ScrollToEndRequested += (_, _) => scrolls++;

        vm.ApplyTranslation(id, TranslationDisplayState.Translated, "确认一下本次发布内容。");

        Assert.Single(vm.Entries);                          // no duplicate card
        Assert.Equal("确认一下本次发布内容。", vm.Entries[0].Translation);
        Assert.True(vm.Entries[0].HasTranslation);
        Assert.False(vm.Entries[0].IsTranslating);
        Assert.Equal(0, scrolls);                           // never forced to bottom
    }

    // UI: failure keeps original text and shows the failed marker; no translation.
    [Fact]
    public void ApplyTranslation_Failure_KeepsOriginal()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        var id = Guid.NewGuid();
        vm.AppendLive(id, DateTimeOffset.Now, "原文", TranslationDisplayState.Translating);

        vm.ApplyTranslation(id, TranslationDisplayState.Failed, null);

        Assert.Equal("原文", vm.Entries[0].Text);
        Assert.True(vm.Entries[0].HasFailed);
        Assert.False(vm.Entries[0].HasTranslation);
    }

    // UI 5: Chinese recognition mode → no translation state, no translation area.
    [Fact]
    public void ChineseMode_NoTranslationArea()
    {
        var vm = NewTimeline();
        vm.BeginSession();
        vm.AppendLive(Guid.NewGuid(), DateTimeOffset.Now, "确认一下", TranslationDisplayState.None);

        Assert.False(vm.Entries[0].HasTranslation);
        Assert.False(vm.Entries[0].IsTranslating);
        Assert.False(vm.Entries[0].HasFailed);
    }

    // UI 4: overlay shows original + translation on two lines, updated in place by segment id.
    [Fact]
    public void Overlay_DoubleLine_InPlace()
    {
        var overlay = new SubtitleOverlayViewModel(new SubtitleSettings { MaxLines = 5 });
        var id = Guid.NewGuid();
        overlay.AddFinal(id, "確認します", translating: true);
        Assert.True(overlay.Lines[0].IsTranslating);

        overlay.ApplyTranslation(id, "确认一下", translating: false);

        Assert.Equal("確認します", overlay.Lines[0].Original);
        Assert.Equal("确认一下", overlay.Lines[0].Translation);
        Assert.True(overlay.Lines[0].HasTranslation);
        Assert.False(overlay.Lines[0].IsTranslating);
        Assert.Single(overlay.Lines); // no duplicate line
    }

    // UI 11: after restart, LoadHistory restores translated text and failed markers from SQLite.
    [Fact]
    public async Task LoadHistory_RestoresTranslationDisplay()
    {
        var sessionId = Guid.NewGuid();
        await _repo.CreateSessionAsync(new MeetingSession { Id = sessionId, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "ja", OutputDirectory = _root }, CancellationToken.None);
        var translated = new TranscriptSegment { Id = Guid.NewGuid(), SessionId = sessionId, StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(2), Language = "ja", Text = "一", Status = TranscriptStatus.Final, CreatedAt = DateTimeOffset.Now };
        var failed = new TranscriptSegment { Id = Guid.NewGuid(), SessionId = sessionId, StartTime = TimeSpan.FromSeconds(3), EndTime = TimeSpan.FromSeconds(4), Language = "ja", Text = "二", Status = TranscriptStatus.Final, CreatedAt = DateTimeOffset.Now };
        await _repo.UpsertSegmentAsync(translated, CancellationToken.None);
        await _repo.UpsertSegmentAsync(failed, CancellationToken.None);
        await _repo.SetSegmentTranslationAsync(translated.Id, "壹", TranscriptStatus.Translated, CancellationToken.None);
        await _repo.SetSegmentTranslationAsync(failed.Id, null, TranscriptStatus.TranslationFailed, CancellationToken.None);

        var vm = NewTimeline();
        await vm.LoadHistoryAsync(sessionId, CancellationToken.None);

        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("壹", vm.Entries[0].Translation);
        Assert.True(vm.Entries[0].HasTranslation);
        Assert.True(vm.Entries[1].HasFailed);
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
