using KikuCaption.Core.Exceptions;
using KikuCaption.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class SqliteTranscriptRepositoryTests
{
    [Fact] // 1 + 11: create session, DB file created
    public async Task CreateSession_PersistsAndCreatesDatabase()
    {
        await using var ctx = new StorageTestContext();
        var session = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None);

        Assert.True(File.Exists(ctx.DbPath));
        var stored = await ctx.Repository.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(SessionStates.Running, stored!.State);
    }

    [Fact] // 2: duplicate create is idempotent
    public async Task CreateSession_Duplicate_IsIdempotent()
    {
        await using var ctx = new StorageTestContext();
        var session = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None);
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None); // no throw
        var stored = await ctx.Repository.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(0, stored!.SegmentCount);
    }

    [Fact] // 3: insert final segment
    public async Task UpsertSegment_InsertsFinal()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(s.Id, "你好世界"), CancellationToken.None);

        var segments = await ctx.Repository.GetSegmentsAsync(s.Id, CancellationToken.None);
        Assert.Single(segments);
        Assert.Equal("你好世界", segments[0].Segment.Text);
        Assert.Equal(1, segments[0].SequenceNumber);
    }

    [Fact] // 4: upsert same id does not duplicate; updates text
    public async Task UpsertSegment_SameId_NoDuplicate()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        var id = Guid.NewGuid();
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(s.Id, "初稿", id: id), CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(s.Id, "修订", id: id), CancellationToken.None);

        var segments = await ctx.Repository.GetSegmentsAsync(s.Id, CancellationToken.None);
        Assert.Single(segments);
        Assert.Equal("修订", segments[0].Segment.Text);
        Assert.Equal(1, segments[0].SequenceNumber); // sequence unchanged
    }

    [Fact] // 5 + 9: multiple segments keep stable order
    public async Task Segments_KeepInsertionOrder()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        for (int i = 0; i < 5; i++)
        {
            await ctx.Repository.UpsertSegmentAsync(
                StorageTestContext.Final(s.Id, $"第{i}句", startSec: i, endSec: i + 1), CancellationToken.None);
        }

        var segments = await ctx.Repository.GetSegmentsAsync(s.Id, CancellationToken.None);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, segments.Select(x => x.SequenceNumber));
        Assert.Equal("第0句", segments[0].Segment.Text);
        Assert.Equal("第4句", segments[4].Segment.Text);
    }

    [Fact] // 6: complete session
    public async Task CompleteSession_SetsCompletedAndEndedAt()
    {
        await using var ctx = new StorageTestContext();
        var s = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(s, CancellationToken.None);
        var ended = DateTimeOffset.Now;
        await ctx.Repository.CompleteSessionAsync(s.Id, ended, CancellationToken.None);

        var stored = await ctx.Repository.GetSessionAsync(s.Id, CancellationToken.None);
        Assert.Equal(SessionStates.Completed, stored!.State);
        Assert.NotNull(stored.Session.EndedAt);
    }

    [Fact] // 7: foreign key constraint
    public async Task UpsertSegment_MissingSession_ThrowsConstraint()
    {
        await using var ctx = new StorageTestContext();
        await ctx.Repository.InitializeAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<StorageException>(
            () => ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(Guid.NewGuid(), "孤儿"), CancellationToken.None));
        Assert.Equal("constraint", ex.Code);
    }

    [Fact] // 8: cancellation
    public async Task Operation_Cancelled_Throws()
    {
        await using var ctx = new StorageTestContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ctx.Repository.CreateSessionAsync(ctx.NewSession(), cts.Token));
    }

    [Fact] // 10: schema version set
    public async Task SchemaVersion_IsSet()
    {
        await using var ctx = new StorageTestContext();
        await ctx.Repository.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ctx.DbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(SqliteSchema.SchemaVersion, version);
    }

    [Fact] // 12: invalid/corrupt database
    public async Task InvalidDatabaseFile_ThrowsStorageException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_baddb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bad.db");
        await File.WriteAllTextAsync(path, "this is definitely not a sqlite database file");

        await using var repo = new SqliteTranscriptRepository(path, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteTranscriptRepository>.Instance);
        var ex = await Assert.ThrowsAsync<StorageException>(() => repo.InitializeAsync(CancellationToken.None));
        Assert.Equal("db_init_failed", ex.Code);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task RecentSessions_AreNewestFirst_AndLimited()
    {
        await using var ctx = new StorageTestContext();
        var now = DateTimeOffset.Now;
        var oldest = ctx.NewSession() with { StartedAt = now.AddHours(-2) };
        var middle = ctx.NewSession() with { StartedAt = now.AddHours(-1) };
        var newest = ctx.NewSession() with { StartedAt = now };

        await ctx.Repository.CreateSessionAsync(oldest, CancellationToken.None);
        await ctx.Repository.CreateSessionAsync(newest, CancellationToken.None);
        await ctx.Repository.CreateSessionAsync(middle, CancellationToken.None);

        var recent = await ctx.Repository.GetRecentSessionsAsync(2, CancellationToken.None);

        Assert.Equal(new[] { newest.Id, middle.Id }, recent.Select(x => x.Session.Id));
    }

    [Fact]
    public async Task DeleteSession_RemovesSessionSegmentsAndTranslationJobs()
    {
        await using var ctx = new StorageTestContext();
        var session = ctx.NewSession();
        var segment = StorageTestContext.Final(session.Id, "delete me");
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(segment, CancellationToken.None);
        await ctx.Repository.CreateTranslationJobAsync(new KikuCaption.Core.Models.TranslationJob
        {
            Id = Guid.NewGuid(), SessionId = session.Id, SegmentId = segment.Id,
            State = KikuCaption.Core.Enums.TranslationJobState.Pending,
            SourceLanguage = "ja", TargetLanguage = "zh", PromptVersion = 1, Model = "m",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await ctx.Repository.DeleteSessionAsync(session.Id, CancellationToken.None);

        Assert.Null(await ctx.Repository.GetSessionAsync(session.Id, CancellationToken.None));
        Assert.Empty(await ctx.Repository.GetSegmentsAsync(session.Id, CancellationToken.None));
        Assert.Empty(await ctx.Repository.GetJobsForSessionAsync(session.Id, CancellationToken.None));
    }
}
