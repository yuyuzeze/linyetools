namespace KikuCaption.Storage.Sqlite;

/// <summary>SQLite schema DDL and version (tracked via PRAGMA user_version).</summary>
public static class SqliteSchema
{
    // v1: initial (M4). v2: TranslationJob gains NextAttemptAt + LastErrorCode + an active-per-segment
    // unique index (M6). Migrations are explicit and preserve existing rows.
    public const int SchemaVersion = 2;

    public const string CreateSql = """
        CREATE TABLE IF NOT EXISTS MeetingSession (
            Id                  TEXT PRIMARY KEY,
            StartedAt           TEXT NOT NULL,
            EndedAt             TEXT,
            RecognitionLanguage TEXT NOT NULL,
            OutputDirectory     TEXT NOT NULL,
            RecordingPath       TEXT,
            State               TEXT NOT NULL,
            CreatedAt           TEXT NOT NULL,
            UpdatedAt           TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS TranscriptSegment (
            Id             TEXT PRIMARY KEY,
            SessionId      TEXT NOT NULL,
            SequenceNumber INTEGER NOT NULL,
            StartTicks     INTEGER NOT NULL,
            EndTicks       INTEGER NOT NULL,
            Language       TEXT NOT NULL,
            Text           TEXT NOT NULL,
            Translation    TEXT,
            Status         TEXT NOT NULL,
            Confidence     REAL,
            CreatedAt      TEXT NOT NULL,
            UpdatedAt      TEXT NOT NULL,
            FOREIGN KEY (SessionId) REFERENCES MeetingSession(Id)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS IX_Segment_Session_Seq
            ON TranscriptSegment (SessionId, SequenceNumber);

        CREATE TABLE IF NOT EXISTS TranslationJob (
            Id            TEXT PRIMARY KEY,
            SessionId     TEXT NOT NULL,
            SegmentId     TEXT NOT NULL,
            State         TEXT NOT NULL,
            AttemptCount  INTEGER NOT NULL DEFAULT 0,
            NextAttemptAt TEXT,
            LastErrorCode TEXT,
            LastError     TEXT,
            CreatedAt     TEXT NOT NULL,
            UpdatedAt     TEXT NOT NULL,
            FOREIGN KEY (SessionId) REFERENCES MeetingSession(Id),
            FOREIGN KEY (SegmentId) REFERENCES TranscriptSegment(Id)
        );

        CREATE INDEX IF NOT EXISTS IX_Session_State ON MeetingSession (State);

        CREATE UNIQUE INDEX IF NOT EXISTS IX_TranslationJob_ActiveSegment
            ON TranslationJob (SegmentId)
            WHERE State IN ('Pending','InProgress','RetryScheduled');
        """;

    /// <summary>Explicit v1 → v2 migration (adds columns + active-per-segment index; keeps data).</summary>
    public const string MigrateV1ToV2Sql = """
        ALTER TABLE TranslationJob ADD COLUMN NextAttemptAt TEXT;
        ALTER TABLE TranslationJob ADD COLUMN LastErrorCode TEXT;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_TranslationJob_ActiveSegment
            ON TranslationJob (SegmentId)
            WHERE State IN ('Pending','InProgress','RetryScheduled');
        """;
}
