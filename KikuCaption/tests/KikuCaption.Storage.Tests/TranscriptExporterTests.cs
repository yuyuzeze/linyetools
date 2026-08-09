using System.Text.Json;
using System.Text.RegularExpressions;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class TranscriptExporterTests
{
    private static async Task<(string Dir, Guid Id)> SeedAsync(StorageTestContext ctx, MeetingSession session,
        IEnumerable<TranscriptSegment> segments)
    {
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None);
        foreach (var seg in segments)
        {
            await ctx.Repository.UpsertSegmentAsync(seg, CancellationToken.None);
        }

        await ctx.Exporter.ExportAsync(session.Id, session.OutputDirectory, CancellationToken.None);
        return (session.OutputDirectory, session.Id);
    }

    private static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    [Fact] // 1: empty session — files exist and are valid/empty
    public async Task EmptySession_ProducesValidEmptyFiles()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, Array.Empty<TranscriptSegment>());

        using var json = JsonDocument.Parse(Read(dir, "transcript.json"));
        Assert.Equal(0, json.RootElement.GetArrayLength());
        Assert.Equal(string.Empty, Read(dir, "transcript.txt"));
        Assert.Equal(string.Empty, Read(dir, "transcript.srt"));
        Assert.True(File.Exists(Path.Combine(dir, "session.json")));
    }

    [Fact] // 2,3,12,13: single + multiple in order, JSON fields, TXT format
    public async Task Segments_Ordered_WithCompleteJsonAndStableTxt()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[]
        {
            StorageTestContext.Final(s.Id, "第一句", 0, 1),
            StorageTestContext.Final(s.Id, "第二句", 1, 2)
        });

        using var json = JsonDocument.Parse(Read(dir, "transcript.json"));
        Assert.Equal(2, json.RootElement.GetArrayLength());
        var first = json.RootElement[0];
        foreach (var field in new[] { "id", "sessionId", "sequenceNumber", "start", "end", "language", "text", "status", "confidence", "createdAt" })
        {
            Assert.True(first.TryGetProperty(field, out _), $"missing {field}");
        }
        Assert.Equal("第一句", first.GetProperty("text").GetString());

        var txt = Read(dir, "transcript.txt").Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, txt.Length);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] 第一句$", txt[0]);
    }

    [Fact] // 4,5: Chinese and Japanese
    public async Task Cjk_TextPreserved()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[]
        {
            StorageTestContext.Final(s.Id, "今天天气很好", 0, 2, language: "zh"),
            StorageTestContext.Final(s.Id, "こんにちは、世界", 2, 4, language: "ja")
        });

        var srt = Read(dir, "transcript.srt");
        Assert.Contains("今天天气很好", srt);
        Assert.Contains("こんにちは、世界", srt);
    }

    [Fact] // 6: newlines within text
    public async Task Newlines_Preserved()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[] { StorageTestContext.Final(s.Id, "第一行\n第二行", 0, 1) });

        using var json = JsonDocument.Parse(Read(dir, "transcript.json"));
        Assert.Contains("\n", json.RootElement[0].GetProperty("text").GetString());
    }

    [Fact] // 7,8: SRT millisecond format and >1h timestamps
    public async Task Srt_FormatAndLongTimestamps()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[]
        {
            StorageTestContext.Final(s.Id, "开始", 0.5, 1.25),
            StorageTestContext.Final(s.Id, "一小时后", 3661.0, 3662.0)
        });

        var srt = Read(dir, "transcript.srt");
        Assert.Matches(@"00:00:00,500 --> 00:00:01,250", srt);
        Assert.Contains("01:01:01,000 --> 01:01:02,000", srt);

        // Strict block validation.
        var blocks = Regex.Matches(srt, @"(\d+)\r\n(\d{2}:\d{2}:\d{2},\d{3}) --> (\d{2}:\d{2}:\d{2},\d{3})\r\n");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("1", blocks[0].Groups[1].Value);
        Assert.Equal("2", blocks[1].Groups[1].Value);
    }

    [Fact] // 9: StartTime > EndTime is clamped, never inverted
    public async Task Srt_ClampsInvertedTimes()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[] { StorageTestContext.Final(s.Id, "边界", startSec: 5, endSec: 3) });

        Assert.Contains("00:00:05,000 --> 00:00:05,000", Read(dir, "transcript.srt"));
    }

    [Fact] // 10,11: partial and empty text never exported
    public async Task PartialAndEmpty_NotExported()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(s.Id, "有效", 0, 1), CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(
            StorageTestContext.Final(s.Id, "临时", 1, 2) with { Status = TranscriptStatus.Partial }, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(s.Id, "   ", 2, 3), CancellationToken.None);
        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);

        using var json = JsonDocument.Parse(Read(s.OutputDirectory, "transcript.json"));
        Assert.Equal(1, json.RootElement.GetArrayLength());
        Assert.Equal("有效", json.RootElement[0].GetProperty("text").GetString());
        Assert.DoesNotContain("临时", Read(s.OutputDirectory, "transcript.srt"));
    }

    [Fact] // 14,15: atomic replace leaves no temp; repeated export is stable
    public async Task RepeatedExport_NoTempLeftover_AndStable()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, id) = await SeedAsync(ctx, s, new[] { StorageTestContext.Final(s.Id, "内容", 0, 1) });

        await ctx.Exporter.ExportAsync(id, dir, CancellationToken.None);
        await ctx.Exporter.ExportAsync(id, dir, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        using var json = JsonDocument.Parse(Read(dir, "transcript.json"));
        Assert.Equal(1, json.RootElement.GetArrayLength());
    }

    [Fact] // session.json content
    public async Task SessionJson_HasVersionsAndCount()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        var (dir, _) = await SeedAsync(ctx, s, new[] { StorageTestContext.Final(s.Id, "一", 0, 1) });

        using var json = JsonDocument.Parse(Read(dir, "session.json"));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("segmentCount").GetInt32());
        Assert.Equal("test-1.0", root.GetProperty("appVersion").GetString());
        Assert.True(root.TryGetProperty("dataFormatVersion", out _));
        Assert.Equal("zh", root.GetProperty("recognitionLanguage").GetString());
    }
}
