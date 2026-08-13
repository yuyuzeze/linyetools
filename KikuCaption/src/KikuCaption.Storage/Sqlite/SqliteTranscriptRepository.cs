using System.Data.Common;
using System.Globalization;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Storage.Sqlite;

/// <summary>
/// SQLite-backed session/segment store. Uses parameterized SQL, foreign keys, WAL, a stable
/// per-session sequence number, idempotent upsert-by-Id, and immediate commit for finals.
/// A single connection is serialized with a semaphore (desktop, low concurrency).
/// </summary>
public sealed class SqliteTranscriptRepository : ITranscriptStore, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly ILogger<SqliteTranscriptRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteConnection? _connection;
    private bool _initialized;

    public SqliteTranscriptRepository(string databasePath, ILogger<SqliteTranscriptRepository> logger)
    {
        _databasePath = databasePath;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken) => await EnsureInitializedAsync(cancellationToken);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
            _connection = new SqliteConnection(connectionString);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

            long version = await ScalarLongAsync("PRAGMA user_version;", cancellationToken).ConfigureAwait(false);
            if (version == 0)
            {
                await ExecuteAsync(SqliteSchema.CreateSql, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync($"PRAGMA user_version={SqliteSchema.SchemaVersion};", cancellationToken).ConfigureAwait(false);
            }
            else if (version > SqliteSchema.SchemaVersion)
            {
                throw new StorageException("schema_newer",
                    $"数据库 schema 版本 {version} 高于本程序支持的 {SqliteSchema.SchemaVersion}，拒绝打开以免破坏数据。");
            }
            else if (version < SqliteSchema.SchemaVersion)
            {
                await MigrateAsync(version, cancellationToken).ConfigureAwait(false);
            }
            // version == SchemaVersion: up to date.

            _initialized = true;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            throw new StorageException("db_init_failed", "数据库初始化失败（可能文件损坏或不是有效的数据库）。", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateSessionAsync(MeetingSession session, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var now = Iso(DateTimeOffset.UtcNow);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO MeetingSession
                    (Id, StartedAt, EndedAt, RecognitionLanguage, OutputDirectory, RecordingPath, State,
                     TranslationEnabled, TranslationSource, TranslationTarget, TranslationModel, CreatedAt, UpdatedAt)
                VALUES (@id, @started, @ended, @lang, @dir, @rec, @state,
                     @tren, @trsrc, @trtgt, @trmodel, @created, @updated)
                ON CONFLICT(Id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("@id", session.Id.ToString("N"));
            command.Parameters.AddWithValue("@started", Iso(session.StartedAt));
            command.Parameters.AddWithValue("@ended", (object?)(session.EndedAt is { } e ? Iso(e) : null) ?? DBNull.Value);
            command.Parameters.AddWithValue("@lang", session.RecognitionLanguage);
            command.Parameters.AddWithValue("@dir", session.OutputDirectory);
            command.Parameters.AddWithValue("@rec", (object?)session.RecordingPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@state", SessionStates.Running);
            command.Parameters.AddWithValue("@tren", (object?)(session.TranslationEnabled is { } te ? (te ? 1 : 0) : null) ?? DBNull.Value);
            command.Parameters.AddWithValue("@trsrc", (object?)session.TranslationSource ?? DBNull.Value);
            command.Parameters.AddWithValue("@trtgt", (object?)session.TranslationTarget ?? DBNull.Value);
            command.Parameters.AddWithValue("@trmodel", (object?)session.TranslationModel ?? DBNull.Value);
            command.Parameters.AddWithValue("@created", now);
            command.Parameters.AddWithValue("@updated", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var now = Iso(DateTimeOffset.UtcNow);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO TranscriptSegment
                    (Id, SessionId, SequenceNumber, StartTicks, EndTicks, Language, Text, Translation, Status, Confidence, CreatedAt, UpdatedAt)
                VALUES
                    (@id, @sid,
                     (SELECT COALESCE(MAX(SequenceNumber), 0) + 1 FROM TranscriptSegment WHERE SessionId = @sid),
                     @start, @end, @lang, @text, @tr, @status, @conf, @created, @updated)
                ON CONFLICT(Id) DO UPDATE SET
                    StartTicks = @start, EndTicks = @end, Language = @lang, Text = @text,
                    Translation = @tr, Status = @status, Confidence = @conf, UpdatedAt = @updated;
                """;
            command.Parameters.AddWithValue("@id", segment.Id.ToString("N"));
            command.Parameters.AddWithValue("@sid", segment.SessionId.ToString("N"));
            command.Parameters.AddWithValue("@start", segment.StartTime.Ticks);
            command.Parameters.AddWithValue("@end", segment.EndTime.Ticks);
            command.Parameters.AddWithValue("@lang", segment.Language);
            command.Parameters.AddWithValue("@text", segment.Text);
            command.Parameters.AddWithValue("@tr", (object?)segment.Translation ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", segment.Status.ToString());
            command.Parameters.AddWithValue("@conf", (object?)segment.Confidence ?? DBNull.Value);
            command.Parameters.AddWithValue("@created", Iso(segment.CreatedAt));
            command.Parameters.AddWithValue("@updated", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (e.g. FK)
        {
            throw new StorageException("constraint", "字幕写入违反约束（会话不存在或外键错误）。", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteSessionAsync(Guid sessionId, DateTimeOffset endedAt, CancellationToken cancellationToken)
        => await SetSessionStateAsync(sessionId, SessionStates.Completed, endedAt, cancellationToken).ConfigureAwait(false);

    public async Task SetSessionStateAsync(Guid sessionId, string state, DateTimeOffset? endedAt, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                UPDATE MeetingSession
                SET State = @state,
                    EndedAt = COALESCE(@ended, EndedAt),
                    UpdatedAt = @updated
                WHERE Id = @id;
                """;
            command.Parameters.AddWithValue("@state", state);
            command.Parameters.AddWithValue("@ended", (object?)(endedAt is { } e ? Iso(e) : null) ?? DBNull.Value);
            command.Parameters.AddWithValue("@updated", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@id", sessionId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetRecordingPathAsync(Guid sessionId, string recordingPath, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "UPDATE MeetingSession SET RecordingPath = @path, UpdatedAt = @updated WHERE Id = @id;";
            command.Parameters.AddWithValue("@path", recordingPath);
            command.Parameters.AddWithValue("@updated", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@id", sessionId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, StartedAt, EndedAt, RecognitionLanguage, OutputDirectory, RecordingPath, State,
                       TranslationEnabled, TranslationSource, TranslationTarget, TranslationModel,
                       (SELECT COUNT(*) FROM TranscriptSegment WHERE SessionId = MeetingSession.Id) AS SegCount
                FROM MeetingSession WHERE Id = @id;
                """;
            command.Parameters.AddWithValue("@id", sessionId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadSession(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredSession?> GetMostRecentSessionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, StartedAt, EndedAt, RecognitionLanguage, OutputDirectory, RecordingPath, State,
                       TranslationEnabled, TranslationSource, TranslationTarget, TranslationModel,
                       (SELECT COUNT(*) FROM TranscriptSegment WHERE SessionId = MeetingSession.Id) AS SegCount
                FROM MeetingSession ORDER BY StartedAt DESC LIMIT 1;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadSession(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredSession>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, 100);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, StartedAt, EndedAt, RecognitionLanguage, OutputDirectory, RecordingPath, State,
                       TranslationEnabled, TranslationSource, TranslationTarget, TranslationModel,
                       (SELECT COUNT(*) FROM TranscriptSegment WHERE SessionId = MeetingSession.Id) AS SegCount
                FROM MeetingSession ORDER BY StartedAt DESC LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", limit);

            var sessions = new List<StoredSession>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(ReadSession(reader));
            }

            return sessions;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredSegment>> GetSegmentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, SessionId, SequenceNumber, StartTicks, EndTicks, Language, Text, Translation, Status, Confidence, CreatedAt
                FROM TranscriptSegment WHERE SessionId = @id ORDER BY SequenceNumber;
                """;
            command.Parameters.AddWithValue("@id", sessionId.ToString("N"));

            var list = new List<StoredSegment>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadSegment(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredSession>> GetIncompleteSessionsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, StartedAt, EndedAt, RecognitionLanguage, OutputDirectory, RecordingPath, State,
                       TranslationEnabled, TranslationSource, TranslationTarget, TranslationModel,
                       (SELECT COUNT(*) FROM TranscriptSegment WHERE SessionId = MeetingSession.Id) AS SegCount
                FROM MeetingSession
                WHERE State NOT IN (@completed, @recovered)
                ORDER BY StartedAt;
                """;
            command.Parameters.AddWithValue("@completed", SessionStates.Completed);
            command.Parameters.AddWithValue("@recovered", SessionStates.Recovered);

            var list = new List<StoredSession>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadSession(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ----- Translation jobs (Milestone 6) -----

    private const string JobColumns =
        "Id, SessionId, SegmentId, State, AttemptCount, NextAttemptAt, LastErrorCode, CreatedAt, UpdatedAt, SourceLanguage, TargetLanguage, PromptVersion, Model";

    public async Task<TranscriptSegment?> GetSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, SessionId, SequenceNumber, StartTicks, EndTicks, Language, Text, Translation, Status, Confidence, CreatedAt
                FROM TranscriptSegment WHERE Id = @id;
                """;
            command.Parameters.AddWithValue("@id", segmentId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return ReadSegment(reader).Segment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO TranslationJob
                    (Id, SessionId, SegmentId, State, AttemptCount, NextAttemptAt, LastErrorCode, LastError,
                     SourceLanguage, TargetLanguage, PromptVersion, Model, CreatedAt, UpdatedAt)
                VALUES (@id, @sid, @seg, @state, @attempt, @next, @code, @code,
                     @src, @tgt, @pv, @model, @created, @updated);
                """;
            BindJob(command, job);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // active-per-segment index or FK
        {
            throw new StorageException("constraint", "已存在该字幕的有效翻译任务或外键错误。", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateTranslationJobAsync(TranslationJob job, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                UPDATE TranslationJob SET
                    State = @state, AttemptCount = @attempt, NextAttemptAt = @next,
                    LastErrorCode = @code, LastError = @code, UpdatedAt = @updated
                WHERE Id = @id;
                """;
            BindJob(command, job);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TranslationJob?> GetActiveJobForSegmentAsync(Guid segmentId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"""
                SELECT {JobColumns} FROM TranslationJob
                WHERE SegmentId = @seg AND State IN ('Pending','InProgress','RetryScheduled')
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@seg", segmentId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TranslationJob>> GetResumableJobsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"""
                SELECT {JobColumns} FROM TranslationJob
                WHERE State IN ('Pending','RetryScheduled') ORDER BY CreatedAt;
                """;
            return await ReadJobsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RecoverInProgressJobsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "UPDATE TranslationJob SET State='Pending', UpdatedAt=@now WHERE State='InProgress';";
            command.Parameters.AddWithValue("@now", Iso(DateTimeOffset.UtcNow));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TranslationJob>> GetJobsForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = $"SELECT {JobColumns} FROM TranslationJob WHERE SessionId = @sid ORDER BY CreatedAt;";
            command.Parameters.AddWithValue("@sid", sessionId.ToString("N"));
            return await ReadJobsAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSegmentTranslationAsync(Guid segmentId, string? translation, TranscriptStatus status, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var command = _connection!.CreateCommand();
            // Only the translation + status change; the original Text is never touched.
            command.CommandText = "UPDATE TranscriptSegment SET Translation=@tr, Status=@status, UpdatedAt=@now WHERE Id=@id;";
            command.Parameters.AddWithValue("@tr", (object?)translation ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", status.ToString());
            command.Parameters.AddWithValue("@now", Iso(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@id", segmentId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void BindJob(SqliteCommand command, TranslationJob job)
    {
        command.Parameters.AddWithValue("@id", job.Id.ToString("N"));
        command.Parameters.AddWithValue("@sid", job.SessionId.ToString("N"));
        command.Parameters.AddWithValue("@seg", job.SegmentId.ToString("N"));
        command.Parameters.AddWithValue("@state", job.State.ToString());
        command.Parameters.AddWithValue("@attempt", job.AttemptCount);
        command.Parameters.AddWithValue("@next", (object?)(job.NextAttemptAt is { } n ? Iso(n) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("@code", (object?)job.LastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@src", job.SourceLanguage);
        command.Parameters.AddWithValue("@tgt", job.TargetLanguage);
        command.Parameters.AddWithValue("@pv", job.PromptVersion);
        command.Parameters.AddWithValue("@model", job.Model ?? string.Empty);
        command.Parameters.AddWithValue("@created", Iso(job.CreatedAt));
        command.Parameters.AddWithValue("@updated", Iso(job.UpdatedAt));
    }

    private static async Task<IReadOnlyList<TranslationJob>> ReadJobsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<TranslationJob>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadJob(reader));
        }

        return list;
    }

    private static TranslationJob ReadJob(DbDataReader r) => new()
    {
        Id = Guid.ParseExact(r.GetString(0), "N"),
        SessionId = Guid.ParseExact(r.GetString(1), "N"),
        SegmentId = Guid.ParseExact(r.GetString(2), "N"),
        State = Enum.Parse<TranslationJobState>(r.GetString(3)),
        AttemptCount = (int)r.GetInt64(4),
        NextAttemptAt = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
        LastErrorCode = r.IsDBNull(6) ? null : r.GetString(6),
        CreatedAt = ParseIso(r.GetString(7)),
        UpdatedAt = ParseIso(r.GetString(8)),
        SourceLanguage = r.GetString(9),
        TargetLanguage = r.GetString(10),
        PromptVersion = (int)r.GetInt64(11),
        Model = r.IsDBNull(12) ? string.Empty : r.GetString(12)
    };

    private static StoredSession ReadSession(DbDataReader reader)
    {
        var session = new MeetingSession
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"),
            StartedAt = ParseIso(reader.GetString(1)),
            EndedAt = reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
            RecognitionLanguage = reader.GetString(3),
            OutputDirectory = reader.GetString(4),
            RecordingPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            TranslationEnabled = reader.IsDBNull(7) ? null : reader.GetInt64(7) != 0,
            TranslationSource = reader.IsDBNull(8) ? null : reader.GetString(8),
            TranslationTarget = reader.IsDBNull(9) ? null : reader.GetString(9),
            TranslationModel = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
        return new StoredSession(session, reader.GetString(6), reader.GetInt32(11));
    }

    private static StoredSegment ReadSegment(DbDataReader reader)
    {
        var segment = new TranscriptSegment
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"),
            SessionId = Guid.ParseExact(reader.GetString(1), "N"),
            StartTime = TimeSpan.FromTicks(reader.GetInt64(3)),
            EndTime = TimeSpan.FromTicks(reader.GetInt64(4)),
            Language = reader.GetString(5),
            Text = reader.GetString(6),
            Translation = reader.IsDBNull(7) ? null : reader.GetString(7),
            Status = Enum.Parse<TranscriptStatus>(reader.GetString(8)),
            Confidence = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            CreatedAt = ParseIso(reader.GetString(10))
        };
        return new StoredSegment(segment, reader.GetInt64(2));
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies explicit, transactional migrations from <paramref name="fromVersion"/> up to the
    /// current <see cref="SqliteSchema.SchemaVersion"/>. Data is preserved; a failure rolls back and
    /// throws (never a silent rebuild/delete). Called under <see cref="_gate"/> during init.
    /// </summary>
    private async Task MigrateAsync(long fromVersion, CancellationToken cancellationToken)
    {
        try
        {
            if (fromVersion == 1)
            {
                await using var tx = await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                using (var command = _connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)tx;
                    command.CommandText = SqliteSchema.MigrateV1ToV2Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync("PRAGMA user_version=2;", cancellationToken).ConfigureAwait(false);
                fromVersion = 2;
                _logger.LogInformation("Migrated database schema v1 → v2 (translation retry columns).");
            }

            if (fromVersion == 2)
            {
                await using var tx = await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                using (var command = _connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)tx;
                    command.CommandText = SqliteSchema.MigrateV2ToV3Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync("PRAGMA user_version=3;", cancellationToken).ConfigureAwait(false);
                fromVersion = 3;
                _logger.LogInformation("Migrated database schema v2 → v3 (translation direction snapshot; legacy jobs default ja→zh).");
            }

            if (fromVersion == 3)
            {
                await using var tx = await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                using (var command = _connection.CreateCommand())
                {
                    command.Transaction = (SqliteTransaction)tx;
                    command.CommandText = SqliteSchema.MigrateV3ToV4Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync("PRAGMA user_version=4;", cancellationToken).ConfigureAwait(false);
                fromVersion = 4;
                _logger.LogInformation("Migrated database schema v3 → v4 (translation model snapshot; legacy jobs fall back to current model).");
            }

            // Future migrations chain here (each guarded by the source version).
        }
        catch (SqliteException ex)
        {
            throw new StorageException("migration_failed",
                $"数据库从版本 {fromVersion} 迁移失败；未做破坏性重建，原数据保留。", ex);
        }
    }

    private async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
