using System.IO;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Summarization;
using KikuCaption.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>UI-R5C acceptance fixes: history target, summary language default/persistence.</summary>
public class MeetingSummaryFixesTests
{
    private sealed class NoService : IMeetingSummaryService
    {
        public Task<MeetingSummaryResult> GenerateAsync(MeetingSummaryRequest r, string f, IProgress<MeetingSummaryProgress>? p, CancellationToken c)
            => throw new NotSupportedException();
    }

    // ---- summary language resolver (scenarios 17-26) --------------------

    [Theory] // 17/18/19: first use follows the UI language
    [InlineData("zh-CN", "zh")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en-US", "en")]
    public void Language_FirstUse_FollowsUi(string ui, string expected)
        => Assert.Equal(expected, SummaryLanguage.Resolve(null, ui));

    [Fact] // 22: a stored user choice wins over the UI language
    public void Language_StoredChoice_Wins()
        => Assert.Equal("en", SummaryLanguage.Resolve("en", "zh-CN"));

    [Fact] // 26: an invalid stored value falls back to the UI language
    public void Language_InvalidStored_FollowsUi()
        => Assert.Equal("ja", SummaryLanguage.Resolve("xx", "ja-JP"));

    [Fact] // distinguishes "never chosen" from "chosen"
    public void Language_HasUserChoice()
    {
        Assert.False(SummaryLanguage.HasUserChoice(null));
        Assert.False(SummaryLanguage.HasUserChoice("xx"));
        Assert.True(SummaryLanguage.HasUserChoice("ja"));
    }

    // ---- timeline displayed-session target (scenarios 1-8) --------------

    private static StoredSegment Seg(long seq) => new(new TranscriptSegment
    {
        Id = Guid.NewGuid(), SessionId = Guid.Empty, StartTime = TimeSpan.FromSeconds(seq), EndTime = TimeSpan.FromSeconds(seq + 1),
        Language = "ja", Text = "t" + seq, Status = TranscriptStatus.Final, CreatedAt = DateTimeOffset.Now
    }, seq);

    [Fact] // 2/3/8: loading a history session sets the target to THAT session's real id + directory
    public async Task Timeline_LoadHistory_SetsTargetToLoadedSession()
    {
        var id = Guid.NewGuid();
        var store = new TestTranscriptStore
        {
            Segments = new[] { Seg(1), Seg(2) },
            Session = new StoredSession(new MeetingSession
            {
                Id = id, StartedAt = DateTimeOffset.Now, RecognitionLanguage = "zh",
                OutputDirectory = @"C:\meetings\history-1", TranslationEnabled = false
            }, "Completed", 2)
        };
        var timeline = new MeetingTimelineViewModel(store);

        await timeline.LoadHistoryAsync(id, CancellationToken.None);

        Assert.NotNull(timeline.DisplayedSession);
        Assert.Equal(id, timeline.DisplayedSession!.SessionId);
        Assert.Equal(@"C:\meetings\history-1", timeline.DisplayedSession.Directory);
        Assert.False(timeline.DisplayedSession.IsLive); // history → idle target, never "running"
    }

    [Fact] // the live session sets a live target; clearing removes it
    public void Timeline_LiveAndClear()
    {
        var timeline = new MeetingTimelineViewModel(new TestTranscriptStore());
        var id = Guid.NewGuid();
        timeline.SetLiveSession(id, @"C:\meetings\live-1", DateTimeOffset.Now, "ja");
        Assert.True(timeline.DisplayedSession!.IsLive);
        Assert.Equal(id, timeline.DisplayedSession.SessionId);

        timeline.ClearDisplayCommand.Execute(null);
        Assert.Null(timeline.DisplayedSession);
    }

    // ---- dialog output-language default + persistence (20-23) -----------

    private (MeetingSummaryDialogViewModel vm, UserSettingsStore store) DialogVm(string dir, LocalizationService loc)
    {
        var coordinator = new MeetingSummaryCoordinator(new TestTranscriptStore(), new NoService(), new MarkdownMeetingSummaryExporter(),
            NullLogger<MeetingSummaryCoordinator>.Instance);
        var settingsStore = new UserSettingsStore(dir);
        var ctx = new SummarySessionContext(Guid.NewGuid(), dir, "ja", DateTimeOffset.Now, SessionState.Completed, 3);
        var vm = new MeetingSummaryDialogViewModel(ctx, coordinator, loc, settingsStore, new TranslationOptions { Model = "m" },
            new MeetingSummaryOptions(), NullLogger.Instance);
        return (vm, settingsStore);
    }

    [Fact] // 17-19: first open follows the UI language and does NOT persist yet
    public void Dialog_FirstOpen_FollowsUi_NoPersist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_dlg", Guid.NewGuid().ToString("N"));
        try
        {
            var loc = new LocalizationService();
            loc.SetLanguage(LocalizedStrings.JaJP);
            var (vm, store) = DialogVm(dir, loc);
            Assert.Equal("ja", vm.OutputLanguage);
            Assert.Null(store.Load().Settings.SummaryOutputLanguage); // not persisted by the default
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact] // 20/21/22: a manual choice persists and, on reopen, wins over the UI language
    public void Dialog_ManualChoice_Persists_AndWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_dlg", Guid.NewGuid().ToString("N"));
        try
        {
            var loc = new LocalizationService(); // zh-CN
            var (vm, store) = DialogVm(dir, loc);
            Assert.Equal("zh", vm.OutputLanguage);

            vm.OutputLanguage = "en"; // user change
            Assert.Equal("en", store.Load().Settings.SummaryOutputLanguage); // persisted

            // Reopen with the UI still zh-CN → the stored choice wins.
            var (vm2, _) = DialogVm(dir, loc);
            Assert.Equal("en", vm2.OutputLanguage);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact] // 27: the settings type still has no secret field
    public void UserSettings_NoSecret_WithSummaryLanguage()
    {
        var s = new UserSettings { SummaryOutputLanguage = "en" };
        Assert.Equal("en", s.SummaryOutputLanguage);
        Assert.DoesNotContain(typeof(UserSettings).GetProperties(), p =>
            p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
