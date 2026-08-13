using KikuCaption.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.Storage.Tests;

public sealed class MeetingSessionDeletionServiceTests
{
    [Fact]
    public async Task Delete_RemovesDatabaseHistoryAndEntireSessionDirectory()
    {
        await using var ctx = new StorageTestContext();
        var session = ctx.NewSession();
        Directory.CreateDirectory(session.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(session.OutputDirectory, "meeting.mp4"), "media");
        await File.WriteAllTextAsync(Path.Combine(session.OutputDirectory, "meeting-summary.md"), "summary");
        await ctx.Repository.CreateSessionAsync(session, CancellationToken.None);
        await ctx.Repository.UpsertSegmentAsync(StorageTestContext.Final(session.Id, "caption"), CancellationToken.None);
        var service = new MeetingSessionDeletionService(ctx.Repository, ctx.Options,
            NullLogger<MeetingSessionDeletionService>.Instance);

        await service.DeleteAsync(session.Id, CancellationToken.None);

        Assert.False(Directory.Exists(session.OutputDirectory));
        Assert.Null(await ctx.Repository.GetSessionAsync(session.Id, CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(ctx.Root, ".deleting-*"));
    }
}
