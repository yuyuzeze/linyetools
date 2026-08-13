using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Xunit;

namespace KikuCaption.App.Tests;

public class RecentMeetingViewModelTests
{
    [Fact]
    public void ProjectsDurationCountArtifacts_AndRelocalizes()
    {
        var loc = new LocalizationService();
        loc.SetLanguage("zh-CN");
        var started = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(9));
        var stored = new StoredSession(new MeetingSession
        {
            Id = Guid.NewGuid(), StartedAt = started, EndedAt = started.AddMinutes(12).AddSeconds(3),
            RecognitionLanguage = "ja", OutputDirectory = @"C:\meetings\one", RecordingPath = @"C:\meetings\one\meeting.mp4"
        }, SessionStates.Completed, 42);

        var vm = new RecentMeetingViewModel(stored, loc, summaryExists: true);
        Assert.Contains("日本語", vm.DetailText);
        Assert.Contains("12:03", vm.DetailText);
        Assert.Contains("42", vm.DetailText);
        Assert.Contains("有录屏", vm.ArtifactText);
        Assert.Contains("有会议要点", vm.ArtifactText);

        loc.SetLanguage("en-US");
        vm.RefreshLocalization();
        Assert.Contains("Japanese", vm.DetailText);
        Assert.Contains("Recording", vm.ArtifactText);
        Assert.Contains("Summary", vm.ArtifactText);
    }
}
