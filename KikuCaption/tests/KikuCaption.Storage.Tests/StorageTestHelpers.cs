using KikuCaption.Core.Enums;
using KikuCaption.Core.Models;
using KikuCaption.Storage;
using KikuCaption.Storage.Export;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KikuCaption.Storage.Tests;

/// <summary>Temp-dir/db scaffolding and synthetic (fictional) CJK data for storage tests.</summary>
internal sealed class StorageTestContext : IAsyncDisposable
{
    public string Root { get; }
    public string DbPath { get; }
    public SqliteTranscriptRepository Repository { get; }
    public TranscriptExporter Exporter { get; }
    public StorageOptions Options { get; }

    public StorageTestContext(double minFreeGb = 0)
    {
        Root = Path.Combine(Path.GetTempPath(), "kiku_storage_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DbPath = Path.Combine(Root, "kikucaption.db");
        Repository = new SqliteTranscriptRepository(DbPath, NullLogger<SqliteTranscriptRepository>.Instance);
        Exporter = new TranscriptExporter(Repository, "test-1.0");
        Options = new StorageOptions { OutputDirectory = Root, BaseDirectory = Root, MinimumFreeSpaceGb = minFreeGb, ExportDebounceMs = 100 };
    }

    public MeetingSession NewSession(Guid? id = null, string language = "zh")
    {
        var sessionId = id ?? Guid.NewGuid();
        return new MeetingSession
        {
            Id = sessionId,
            StartedAt = DateTimeOffset.Now,
            RecognitionLanguage = language,
            OutputDirectory = SessionPaths.BuildSessionDirectory(Root, new MeetingSession
            {
                Id = sessionId,
                StartedAt = DateTimeOffset.Now,
                RecognitionLanguage = language,
                OutputDirectory = Root
            })
        };
    }

    public static TranscriptSegment Final(Guid sessionId, string text, double startSec = 0, double endSec = 1,
        Guid? id = null, string language = "zh", double? confidence = 0.9)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            SessionId = sessionId,
            StartTime = TimeSpan.FromSeconds(startSec),
            EndTime = TimeSpan.FromSeconds(endSec),
            Language = language,
            Text = text,
            Status = TranscriptStatus.Final,
            Confidence = confidence,
            CreatedAt = DateTimeOffset.Now
        };

    public async ValueTask DisposeAsync()
    {
        await Repository.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
