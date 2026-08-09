using System.Text.Json;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Recovery;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

public class SessionRecoveryServiceTests
{
    private static SessionRecoveryService Recovery(StorageTestContext ctx)
        => new(ctx.Repository, ctx.Exporter, NullLogger<SessionRecoveryService>.Instance);

    private static async Task<MeetingSession> SeedRunningAsync(StorageTestContext ctx, int segmentCount)
    {
        var session = ctx.NewSession();
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None); // stays Running
        for (int i = 0; i < segmentCount; i++)
        {
            await ctx.Repository.UpsertSegmentAsync(
                StorageTestContext.Final(session.Id, $"第{i}句", i, i + 1), CancellationToken.None);
        }

        return session;
    }

    [Fact] // 1 + 2: running session discovered and rebuilt from SQLite
    public async Task RunningSession_Recovered_FilesRebuilt()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 2);

        var result = await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.Equal(1, result.RecoveredCount);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.OutputDirectory, "transcript.json")));
        Assert.Equal(2, json.RootElement.GetArrayLength());

        var stored = await ctx.Repository.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(SessionStates.Recovered, stored!.State);
    }

    [Fact] // 3: idempotent
    public async Task RepeatedRecovery_Idempotent()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 3);

        var first = await Recovery(ctx).RecoverAsync(CancellationToken.None);
        var second = await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.Equal(1, first.RecoveredCount);
        Assert.Equal(0, second.RecoveredCount); // already Recovered → excluded
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.OutputDirectory, "transcript.json")));
        Assert.Equal(3, json.RootElement.GetArrayLength());
    }

    [Fact] // 4: missing single export file is rebuilt
    public async Task MissingExportFile_Rebuilt()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 1);
        await Recovery(ctx).RecoverAsync(CancellationToken.None);
        File.Delete(Path.Combine(session.OutputDirectory, "transcript.srt"));

        // Re-seed as running to trigger recovery again (simulate second crash), then recover.
        await ctx.Repository.SetSessionStateAsync(session.Id, SessionStates.Running, null, CancellationToken.None);
        await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(session.OutputDirectory, "transcript.srt")));
    }

    [Fact] // 5: corrupt JSON is backed up, then rebuilt
    public async Task CorruptJson_BackedUp_AndRebuilt()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 1);
        Directory.CreateDirectory(session.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(session.OutputDirectory, "transcript.json"), "{ this is not valid json");

        await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.NotEmpty(Directory.GetFiles(session.OutputDirectory, "transcript.json.corrupt-*.bak"));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.OutputDirectory, "transcript.json")));
        Assert.Equal(1, json.RootElement.GetArrayLength());
    }

    [Fact] // 6: leftover temp files removed
    public async Task StaleTempFiles_Removed()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 1);
        Directory.CreateDirectory(session.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(session.OutputDirectory, "transcript.json.tmp"), "leftover");

        await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.Empty(Directory.GetFiles(session.OutputDirectory, "*.tmp"));
    }

    [Fact] // 7: incomplete session with no segments still rebuilt
    public async Task NoSegmentsSession_Rebuilt()
    {
        await using var ctx = new StorageTestContext();
        var session = await SeedRunningAsync(ctx, 0);

        var result = await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.Equal(1, result.RecoveredCount);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.OutputDirectory, "transcript.json")));
        Assert.Equal(0, json.RootElement.GetArrayLength());
    }

    [Fact] // 8: corrupt database → error, not false success
    public async Task CorruptDatabase_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_rec_baddb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "bad.db");
        await File.WriteAllTextAsync(dbPath, "not a database");
        await using var repo = new SqliteTranscriptRepository(dbPath, NullLogger<SqliteTranscriptRepository>.Instance);
        var exporter = new KikuCaption.Storage.Export.TranscriptExporter(repo, "t");
        var recovery = new SessionRecoveryService(repo, exporter, NullLogger<SessionRecoveryService>.Instance);

        await Assert.ThrowsAsync<StorageException>(() => recovery.RecoverAsync(CancellationToken.None));
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact] // 9: one session's failure does not block others
    public async Task OneSessionFails_OthersRecover()
    {
        await using var ctx = new StorageTestContext();
        var good = await SeedRunningAsync(ctx, 1);

        // A second running session whose output path is actually a file → CreateDirectory fails.
        var bad = ctx.NewSession();
        var blocker = bad.OutputDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "blocker");
        await ctx.Repository.CreateSessionAsync(bad, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(bad.Id, "x"), CancellationToken.None);

        var result = await Recovery(ctx).RecoverAsync(CancellationToken.None);

        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(File.Exists(Path.Combine(good.OutputDirectory, "transcript.json")));
    }
}
