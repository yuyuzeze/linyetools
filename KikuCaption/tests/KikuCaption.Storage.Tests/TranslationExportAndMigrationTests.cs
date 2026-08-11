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

    // UI-R4A: session.json records the direction; translation.srt carries the target-language text.
    [Theory]
    [InlineData("ja", "en", "This is a translation.")]
    [InlineData("zh", "ja", "これは翻訳です。")]
    [InlineData("ja", "zh", "这是翻译。")]
    public async Task Export_RecordsDirection_AndTargetLanguageSrt(string src, string tgt, string translation)
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession(language: src) with { TranslationEnabled = true, TranslationSource = src, TranslationTarget = tgt };
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(
            Ja(s.Id, "原文テキスト", 1, TranscriptStatus.Translated, translation), CancellationToken.None);

        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);

        var sessionJson = Read(s.OutputDirectory, "session.json");
        Assert.Contains($"\"translationSource\": \"{src}\"", sessionJson);
        Assert.Contains($"\"translationTarget\": \"{tgt}\"", sessionJson);
        Assert.Contains("\"translationEnabled\": true", sessionJson);
        Assert.Contains("\"dataFormatVersion\": 2", sessionJson);

        Assert.Contains(translation, Read(s.OutputDirectory, "translation.srt")); // target-language content
    }

    // UI-R4A: same-language session produces no translation content and is marked disabled.
    [Fact]
    public async Task Export_SameLanguage_NoTranslationSrt_MarkedDisabled()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession(language: "ja") with { TranslationEnabled = false, TranslationSource = "ja", TranslationTarget = "ja" };
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(Ja(s.Id, "同じ言語", 1), CancellationToken.None); // Final, no translation

        await ctx.Exporter.ExportAsync(s.Id, s.OutputDirectory, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(s.OutputDirectory, "translation.srt"))); // no translation file
        var sessionJson = Read(s.OutputDirectory, "session.json");
        Assert.Contains("\"translationEnabled\": false", sessionJson);
        Assert.Contains("\"translatedCount\": 0", sessionJson);
    }

    // Schema migration v1 → v4 preserves existing rows, adds all the new columns, defaults legacy
    // translation jobs to ja→zh / prompt v1, and leaves the model empty (UI-R4A).
    [Fact]
    public async Task Migration_V1ToV4_PreservesData_LegacyJaZh()
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
            Assert.Null(jobs[0].NextAttemptAt);               // v2 column present + null
            // v3 columns present with the legacy default direction; v4 model empty.
            Assert.Equal("ja", jobs[0].SourceLanguage);
            Assert.Equal("zh", jobs[0].TargetLanguage);
            Assert.Equal(1, jobs[0].PromptVersion);
            Assert.Equal("", jobs[0].Model);

            var seg = await repo.GetSegmentAsync(Guid.ParseExact(segId, "N"), CancellationToken.None);
            Assert.Equal("原文", seg!.Text);
        }

        // user_version bumped to the current schema version (4).
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(4L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // Schema migration v3 → v4 adds the model columns and preserves the existing v3 direction snapshot.
    [Fact]
    public async Task Migration_V3ToV4_PreservesData_AddsModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_migr34", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "v3.db");
        var sessionId = Guid.NewGuid().ToString("N");
        var segId = Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid().ToString("N");

        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // A hand-built v3 database: direction columns exist, model columns do not.
            cmd.CommandText = $"""
                CREATE TABLE MeetingSession (Id TEXT PRIMARY KEY, StartedAt TEXT NOT NULL, EndedAt TEXT,
                    RecognitionLanguage TEXT NOT NULL, OutputDirectory TEXT NOT NULL, RecordingPath TEXT,
                    State TEXT NOT NULL, TranslationEnabled INTEGER, TranslationSource TEXT, TranslationTarget TEXT,
                    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE TranscriptSegment (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, SequenceNumber INTEGER NOT NULL,
                    StartTicks INTEGER NOT NULL, EndTicks INTEGER NOT NULL, Language TEXT NOT NULL, Text TEXT NOT NULL,
                    Translation TEXT, Status TEXT NOT NULL, Confidence REAL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE TranslationJob (Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, SegmentId TEXT NOT NULL,
                    State TEXT NOT NULL, AttemptCount INTEGER NOT NULL DEFAULT 0, NextAttemptAt TEXT, LastErrorCode TEXT, LastError TEXT,
                    SourceLanguage TEXT NOT NULL DEFAULT 'ja', TargetLanguage TEXT NOT NULL DEFAULT 'zh', PromptVersion INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                INSERT INTO MeetingSession (Id,StartedAt,RecognitionLanguage,OutputDirectory,State,TranslationEnabled,TranslationSource,TranslationTarget,CreatedAt,UpdatedAt)
                    VALUES ('{sessionId}','2026-08-09T10:00:00+00:00','ja','{dir}','Running',1,'ja','en','2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                INSERT INTO TranscriptSegment VALUES ('{segId}','{sessionId}',1,0,10000000,'ja','原文',NULL,'Final',NULL,'2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                INSERT INTO TranslationJob (Id,SessionId,SegmentId,State,AttemptCount,SourceLanguage,TargetLanguage,PromptVersion,CreatedAt,UpdatedAt)
                    VALUES ('{jobId}','{sessionId}','{segId}','Pending',0,'ja','en',2,'2026-08-09T10:00:00+00:00','2026-08-09T10:00:00+00:00');
                PRAGMA user_version=3;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var repo = new SqliteTranscriptRepository(dbPath, NullLogger<SqliteTranscriptRepository>.Instance))
        {
            await repo.InitializeAsync(CancellationToken.None);
            var jobs = await repo.GetJobsForSessionAsync(Guid.ParseExact(sessionId, "N"), CancellationToken.None);
            Assert.Single(jobs);
            Assert.Equal("ja", jobs[0].SourceLanguage);   // v3 direction preserved
            Assert.Equal("en", jobs[0].TargetLanguage);
            Assert.Equal(2, jobs[0].PromptVersion);
            Assert.Equal("", jobs[0].Model);              // new v4 column, empty for legacy
        }

        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(4L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
