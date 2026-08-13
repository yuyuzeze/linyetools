using System.Globalization;
using System.Text;
using KikuCaption.Core.Enums;

namespace KikuCaption.Summarization;

/// <summary>Renders a <see cref="MeetingSummaryDocument"/> to Markdown and writes it atomically.</summary>
public interface IMeetingSummaryExporter
{
    string DefaultFileName { get; }
    string VersionedFileName(DateTimeOffset timestamp);

    /// <summary>A versioned file name that does not collide with an existing file in the directory
    /// (adds -2, -3, … within the same second). Confined to the session directory.</summary>
    string UniqueVersionedFileName(string sessionDirectory, DateTimeOffset timestamp);

    /// <summary>Renders the document to a Markdown string in its output language.</summary>
    string Render(MeetingSummaryDocument document);

    /// <summary>
    /// Renders and atomically writes the Markdown into <paramref name="sessionDirectory"/> under
    /// <paramref name="fileName"/> (a simple name, no path separators). Returns the full path. Guards
    /// against path traversal and never leaves a half-written file.
    /// </summary>
    Task<string> WriteAsync(MeetingSummaryDocument document, string sessionDirectory, string fileName, CancellationToken cancellationToken);
}

/// <summary>
/// Markdown exporter (UI-R5C §11). Fixed section templates per meeting type, localized into the
/// document's output language, with placeholders for empty sections and "not specified" owners/dates.
/// The write is atomic (temp file in the same directory → flush → replace) and confined to the session
/// directory (no traversal). It never touches meeting.mp4 / transcript.* / session.json / SQLite.
/// </summary>
public sealed class MarkdownMeetingSummaryExporter : IMeetingSummaryExporter
{
    public string DefaultFileName => "meeting-summary.md";

    public string VersionedFileName(DateTimeOffset timestamp)
        => "meeting-summary-" + timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md";

    public string UniqueVersionedFileName(string sessionDirectory, DateTimeOffset timestamp)
    {
        var baseName = VersionedFileName(timestamp);
        if (!File.Exists(ResolveSafePath(sessionDirectory, baseName)))
        {
            return baseName;
        }

        // Same-second collision → append -2, -3, … (still inside the session directory).
        var stem = baseName[..^3]; // drop ".md"
        for (int n = 2; n < 10000; n++)
        {
            var candidate = $"{stem}-{n}.md";
            if (!File.Exists(ResolveSafePath(sessionDirectory, candidate)))
            {
                return candidate;
            }
        }

        return baseName; // pathological fallback (10k same-second versions)
    }

    public string Render(MeetingSummaryDocument d)
    {
        var s = Strings.For(d.OutputLanguage);
        var sb = new StringBuilder();

        sb.Append("# ").Append(s.Title).Append("\n\n");
        AppendMetadata(sb, d, s);
        sb.Append('\n');

        if (d.MeetingType == MeetingType.SinglePresenter)
        {
            Section(sb, s.Overview, Bullets(Single(d.Sections.Overview), s));
            Section(sb, s.Topics, Bullets(d.Sections.Topics, s));
            Section(sb, s.KeyPoints, Bullets(d.Sections.KeyPoints, s));
            Section(sb, s.ProcessSteps, Bullets(d.Sections.ProcessSteps, s));
            Section(sb, s.Conclusions, Bullets(d.Sections.Conclusions, s));
            Section(sb, s.Notes, Bullets(d.Sections.Risks, s));
        }
        else
        {
            Section(sb, s.GroupOverview, Bullets(Single(d.Sections.Overview), s));
            Section(sb, s.GroupTopics, Bullets(d.Sections.Topics, s));
            Section(sb, s.GroupKeyPoints, Bullets(d.Sections.KeyPoints, s));
            Section(sb, s.Decisions, Bullets(d.Sections.Decisions, s));
            Section(sb, s.ActionItems, ActionTable(d.Sections.ActionItems, d.OutputLanguage, s));
            Section(sb, s.UnresolvedQuestions, Bullets(d.Sections.UnresolvedQuestions, s));
            Section(sb, s.Risks, Bullets(d.Sections.Risks, s));
        }

        return sb.ToString();
    }

    public async Task<string> WriteAsync(MeetingSummaryDocument document, string sessionDirectory, string fileName, CancellationToken cancellationToken)
    {
        var target = ResolveSafePath(sessionDirectory, fileName);
        Directory.CreateDirectory(sessionDirectory);

        var markdown = Render(document);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true); // atomic replace; never a half-written .md
            return target;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Validates the file name and confines the output to the session directory.</summary>
    public static string ResolveSafePath(string sessionDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || fileName != Path.GetFileName(fileName))
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "非法的输出文件名。");
        }

        var dirFull = Path.GetFullPath(sessionDirectory);
        var full = Path.GetFullPath(Path.Combine(dirFull, fileName));
        var prefix = dirFull.EndsWith(Path.DirectorySeparatorChar) ? dirFull : dirFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new MeetingSummaryException(TranslationErrorCode.InvalidConfig, "输出路径越界。");
        }

        return full;
    }

    private static void AppendMetadata(StringBuilder sb, MeetingSummaryDocument d, Strings s)
    {
        string type = d.MeetingType == MeetingType.SinglePresenter ? s.SinglePresenter : s.GroupDiscussion;
        // Same rule as the dialog: a positive span shows the range; otherwise "unknown" (never a wrong 00:00).
        string range = d.End > d.Start ? $"{Fmt(d.Start)} – {Fmt(d.End)}" : s.Unknown;
        sb.Append("- ").Append(s.Generated).Append("：").Append(d.GeneratedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- ").Append(s.SessionDate).Append("：").Append(d.SessionDate.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("- ").Append(s.MeetingTypeLabel).Append("：").Append(type).Append('\n');
        sb.Append("- ").Append(s.SummaryLanguage).Append("：").Append(s.LanguageName).Append('\n');
        sb.Append("- ").Append(s.Model).Append("：").Append(d.Model).Append('\n');
        sb.Append("- ").Append(s.PromptVersion).Append("：").Append(d.PromptVersion).Append('\n');
        sb.Append("- ").Append(s.SegmentCount).Append("：").Append(d.SegmentCount).Append('\n');
        sb.Append("- ").Append(s.TimeRange).Append("：").Append(range).Append('\n');
        sb.Append("- ").Append(s.DataSource).Append('\n');
    }

    private static void Section(StringBuilder sb, string heading, string body)
    {
        sb.Append("## ").Append(heading).Append("\n\n").Append(body).Append("\n\n");
    }

    private static IReadOnlyList<string> Single(string value)
        => string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };

    private static string Bullets(IReadOnlyList<string> items, Strings s)
    {
        if (items.Count == 0)
        {
            return s.Empty;
        }
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append("- ").Append(Sanitize(item)).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string ActionTable(IReadOnlyList<MeetingActionItem> items, string lang, Strings s)
    {
        if (items.Count == 0)
        {
            return s.Empty;
        }
        var na = MeetingSummaryPrompt.NotSpecified(lang);
        var sb = new StringBuilder();
        sb.Append("| ").Append(s.ColTask).Append(" | ").Append(s.ColOwner).Append(" | ").Append(s.ColDue).Append(" |\n");
        sb.Append("|---|---|---|\n");
        foreach (var it in items)
        {
            var owner = string.IsNullOrWhiteSpace(it.Owner) ? na : Sanitize(it.Owner);
            var due = string.IsNullOrWhiteSpace(it.Due) ? na : Sanitize(it.Due);
            sb.Append("| ").Append(Sanitize(it.Task)).Append(" | ").Append(owner).Append(" | ").Append(due).Append(" |\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    // Model content is plain text: neutralize pipes/newlines that would break Markdown structure.
    private static string Sanitize(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");

    private static string Fmt(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

    /// <summary>Localized headings/labels for the Markdown document, keyed by output language.</summary>
    private sealed record Strings(
        string LanguageName, string Title, string Generated, string SessionDate, string MeetingTypeLabel,
        string SummaryLanguage, string Model, string PromptVersion, string SegmentCount, string TimeRange,
        string DataSource, string SinglePresenter, string GroupDiscussion, string Empty, string Unknown,
        string Overview, string GroupOverview, string Topics, string GroupTopics, string KeyPoints, string GroupKeyPoints,
        string ProcessSteps, string Conclusions, string Notes,
        string Decisions, string ActionItems, string UnresolvedQuestions, string Risks,
        string ColTask, string ColOwner, string ColDue)
    {
        public static Strings For(string lang) => lang switch
        {
            "ja" => new("日本語", "会議まとめ", "生成時刻", "会議日", "会議形式", "要約言語", "使用モデル",
                "プロンプト版", "字幕件数", "字幕時間範囲", "データソース：確認済みの原文字幕", "単独説明", "複数人ディスカッション",
                "該当する内容は抽出されませんでした", "不明",
                "内容概要", "会議概要", "主なトピック", "討論トピック", "重要ポイント", "主な意見", "手順", "結論", "注意事項",
                "決定事項", "アクション項目", "未解決の質問", "リスクと注意事項", "項目", "担当", "期限"),
            "en" => new("English", "Meeting Summary", "Generated", "Session date", "Meeting type", "Summary language", "Model",
                "Prompt version", "Segment count", "Time range", "Source: confirmed original captions", "Single presenter", "Group discussion",
                "No relevant content extracted.", "Unknown",
                "Overview", "Meeting overview", "Topics", "Discussion topics", "Key points", "Key opinions", "Process steps", "Conclusions", "Notes",
                "Decisions", "Action items", "Unresolved questions", "Risks and notes", "Task", "Owner", "Due"),
            _ => new("简体中文", "会议要点", "生成时间", "会话日期", "会议形式", "摘要语言", "使用模型",
                "Prompt 版本", "字幕条数", "字幕时间范围", "数据来源：已确认的原文字幕", "单人讲解", "多人讨论",
                "未提取到相关内容。", "未知",
                "内容概要", "会议概述", "主要主题", "讨论主题", "关键知识点", "主要观点", "操作流程", "结论", "注意事项",
                "决定事项", "待办事项", "未解决问题", "风险与注意事项", "事项", "负责人", "截止时间")
        };
    }
}
