using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class TranslationExportAndMigrationTests
{
    private static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    private static TranscriptSegment Ja(Guid sessionId, string text, int sec, TranscriptStatus status = TranscriptStatus.Final, string? translation = null)
        => new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            StartTime = TimeSpan.FromSeconds(sec),
            EndTime = TimeSpan.FromSeconds(sec + 2),
            Language = "ja",
            Text = text,
            Translation = translation,
            Status = status,
            CreatedAt = DateTimeOffset.Now
        };

    // export 6/7/8/9: translation.srt — only translated, correct times, order, UTF-8, skip failed.
    [Fact]
    public async Task TranslationSrt_OnlyTranslated_InOrder_WithOriginalTimes()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);

        var a = Ja(s.Id, "一つ目", 1, TranscriptStatus.Translated, "第一句");
        var b = Ja(s.Id, "二つ目", 5, TranscriptStatus.Final);            // not translated → skipped
        var c = Ja(s.Id, "三つ目", 9, TranscriptStatus.TranslationFailed); // failed → skipped
        var d = Ja(s.Id, "四つ目", 13, TranscriptStatus.Translated, "第四句");
        foreach (var seg in new[] { a, b, c, d })
        {
            await ctx.Repository.UpsertSegmentAsync(seg, CancellationToken.None);
        }

        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);

        var srt = Read(s.OutputDirectory, "translation.srt");
        Assert.Contains("1\r\n00:00:01,000 --> 00:00:03,000\r\n第一句", srt);
        Assert.Contains("2\r\n00:00:13,000 --> 00:00:15,000\r\n第四句", srt); // contiguous renumber
        Assert.DoesNotContain("二つ目", srt);
        Assert.DoesNotContain("三つ目", srt);
        Assert.DoesNotContain("第三", srt);

        // transcript.srt keeps the ORIGINAL text for translated lines (lifecycle preserved).
        Assert.Contains("一つ目", Read(s.OutputDirectory, "transcript.srt"));
    }

    // export 10/11: repeated export is not duplicated; rebuild from SQLite reproduces file.
    [Fact]
    public async Task TranslationSrt_RepeatedExport_NotDuplicated()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(Ja(s.Id, "会議", 1, TranscriptStatus.Translated, "会议"), CancellationToken.None);

        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);
        var first = Read(s.OutputDirectory, "translation.srt");
        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);
        var second = Read(s.OutputDirectory, "translation.srt");

        Assert.Equal(first, second);
        Assert.Equal(1, first.Split("-->").Length - 1); // exactly one cue
    }

    // transcript.json carries both original and translation; session.json has translatedCount.
    [Fact]
    public async Task TranscriptJson_ContainsTranslation_AndCount()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(Ja(s.Id, "確認", 1, TranscriptStatus.Translated, "确认"), CancellationToken.None);

        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);

        Assert.Contains("确认", Read(s.OutputDirectory, "transcript.json"));
        Assert.Contains("確認", Read(s.OutputDirectory, "transcript.json"));
        Assert.Contains("\"translatedCount\": 1", Read(s.OutputDirectory, "session.json"));
    }

    // Schema migration v1 → v2 preserves existing rows and adds the new columns.
    [Fact]
    public async Task Migration_V1ToV2_PreservesData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_migr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "v1.db");
        var sessionId = Guid.NewGuid().ToString("N");
        var segId = Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid().ToString("N");

        // Build a v1 database by hand (old TranslationJob without NextAttemptAt/LastErrorCode).
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE MeetingSession (Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, EndedAt TEXT,
                    RecognitionLanguage TEXT NOT NULL, OutputDirectory TEXT NOT NULL, RecordingPath TEXT,
                    State TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE TranscriptSegment (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, SequenceNumber INTEGER NOT NULL,
                    StartTicks INTEGER NOT NULL, EndTicks INTEGER NOT NULL, Language TEXT NOT NULL, Text TEXT NOT NULL,
                    Translation TEXT, Status TEXT NOT NULL, Confidence REAL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE TranslationJob (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, SegmentId TEXT NOT NULL,
                    State TEXT NOT NULL, AttemptCount INTEGER NOT NULL DEFAULT 0, LastError TEXT,
                    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                INSERT INTO MeetingSession VALUES ('{sessionId}','2026-08-09T10:00:00+00:00',NULL,'ja','{dir}',NULL,'Running','2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                INSERT INTO TranscriptSegment VALUES ('{segId}','{sessionId}',1,0,10000000,'ja','原文',NULL,'Final',NULL,'2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                INSERT INTO TranslationJob (Id,SessionId,SegmentId,State,AttemptCount,LastError,CreatedAt,UpdatedAt)
                    VALUES ('{jobId}','{sessionId}','{segId}','Pending',0,NULL,'2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                PRAGMA user_version=1;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Open with the repository → triggers the migration.
        await using (var repo = new SqliteTranscriptRepository(dbPath, NullLogger<SqliteTranscriptRepository>.Instance))
        {
            await repo.InitializeAsync(CancellationToken.None);

            var jobs = await repo.GetJobsForSessionAsync(Guid.ParseExact(sessionId, "N"), CancellationToken.None);
            Assert.Single(jobs);                              // old data preserved
            Assert.Equal(TranslationJobState.Pending, jobs[0].State);
            Assert.Null(jobs[0].NextAttemptAt);               // new column present + null

            var seg = await repo.GetSegmentAsync(Guid.ParseExact(segId, "N"), CancellationToken.None);
            Assert.Equal("原文", seg!.Text);
        }

        // user_version bumped to 2.
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(2L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
