using System.IO;
using System.Linq;
using Xunit;

namespace KikuCaption.Summarization.Tests;

/// <summary>UI-R5C: Markdown templates, placeholders, atomic write, and path-traversal guard.</summary>
public class MarkdownExporterTests : IDisposable
{
    private readonly string _dir;
    private readonly MarkdownMeetingSummaryExporter _exporter = new();

    public MarkdownExporterTests()
        => _dir = Path.Combine(Path.GetTempPath(), "kiku_sum", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MeetingSummaryDocument Doc(MeetingType type, string lang, MeetingSummarySections? sections = null)
        => new()
        {
            SessionId = Guid.NewGuid(),
            MeetingType = type,
            OutputLanguage = lang,
            Model = "gpt-x",
            PromptVersion = MeetingSummaryPrompt.Version,
            GeneratedAt = DateTimeOffset.Now,
            SessionDate = DateTimeOffset.Now,
            SegmentCount = 12,
            Start = TimeSpan.Zero,
            End = TimeSpan.FromMinutes(30),
            Sections = sections ?? new MeetingSummarySections()
        };

    [Fact] // scenario 10: single-presenter template sections
    public void Single_Template_HasSections()
    {
        var md = _exporter.Render(Doc(MeetingType.SinglePresenter, "zh",
            new MeetingSummarySections { Overview = "概要内容", KeyPoints = new[] { "要点1" } }));
        Assert.Contains("# 会议要点", md);
        Assert.Contains("## 内容概要", md);
        Assert.Contains("## 主要主题", md);
        Assert.Contains("## 关键知识点", md);
        Assert.Contains("## 操作流程", md);
        Assert.Contains("## 结论", md);
        Assert.Contains("## 注意事项", md);
        Assert.Contains("- 要点1", md);
    }

    [Fact] // scenario 11: group-discussion template + action-item table
    public void Discussion_Template_HasActionTable()
    {
        var md = _exporter.Render(Doc(MeetingType.GroupDiscussion, "zh",
            new MeetingSummarySections { ActionItems = new[] { new MeetingActionItem("部署上线", "", "") } }));
        Assert.Contains("## 会议概述", md);
        Assert.Contains("## 讨论主题", md);
        Assert.Contains("## 决定事项", md);
        Assert.Contains("## 待办事项", md);
        Assert.Contains("| 事项 | 负责人 | 截止时间 |", md);
        Assert.Contains("| 部署上线 | 未明确 | 未明确 |", md); // scenario 13/14
    }

    [Fact] // scenario 12: no Speaker/role structure is ever emitted
    public void Output_HasNoSpeakerLabels()
    {
        var md = _exporter.Render(Doc(MeetingType.GroupDiscussion, "en",
            new MeetingSummarySections { KeyPoints = new[] { "point" } }));
        Assert.DoesNotContain("Speaker", md);
        Assert.DoesNotContain("发言人", md);
    }

    [Fact] // empty section shows the localized placeholder (fixed sections never removed)
    public void EmptySection_ShowsPlaceholder()
    {
        var md = _exporter.Render(Doc(MeetingType.SinglePresenter, "zh"));
        Assert.Contains("未提取到相关内容。", md);
    }

    [Theory] // scenario 15/16/17: localized titles
    [InlineData("zh", "# 会议要点")]
    [InlineData("ja", "# 会議まとめ")]
    [InlineData("en", "# Meeting Summary")]
    public void Title_IsLocalized(string lang, string title)
        => Assert.Contains(title, _exporter.Render(Doc(MeetingType.SinglePresenter, lang)));

    [Fact] // metadata records the prompt version + data source (scenario 31)
    public void Metadata_HasPromptVersionAndSource()
    {
        var md = _exporter.Render(Doc(MeetingType.SinglePresenter, "zh"));
        Assert.Contains("Prompt 版本：1", md);
        Assert.Contains("数据来源：已确认的原文字幕", md);
    }

    [Fact] // scenario 40: atomic write leaves the file and no temp
    public async Task Write_IsAtomic()
    {
        var path = await _exporter.WriteAsync(Doc(MeetingType.SinglePresenter, "zh"), _dir, _exporter.DefaultFileName, CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.EndsWith("meeting-summary.md", path);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact] // versioned file name format
    public void VersionedFileName_Format()
    {
        var name = _exporter.VersionedFileName(new DateTimeOffset(2026, 8, 12, 9, 30, 5, TimeSpan.Zero));
        Assert.Equal("meeting-summary-20260812-093005.md", name);
    }

    [Theory] // scenario 44: path-traversal / illegal names are rejected
    [InlineData("../evil.md")]
    [InlineData("..\\evil.md")]
    [InlineData("sub/evil.md")]
    [InlineData("C:\\evil.md")]
    public void ResolveSafePath_RejectsTraversal(string bad)
        => Assert.Throws<MeetingSummaryException>(() => MarkdownMeetingSummaryExporter.ResolveSafePath(_dir, bad));

    [Fact] // a plain name inside the session directory is allowed
    public void ResolveSafePath_AllowsPlainName()
    {
        var p = MarkdownMeetingSummaryExporter.ResolveSafePath(_dir, "meeting-summary.md");
        Assert.StartsWith(Path.GetFullPath(_dir), p);
    }
}
