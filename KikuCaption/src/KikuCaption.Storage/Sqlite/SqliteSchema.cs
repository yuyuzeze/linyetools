namespace KikuCaption.Storage.Sqlite;

/// <summary>SQLite schema DDL and version (tracked via PRAGMA user_version).</summary>
public static class SqliteSchema
{
    // v1: initial (M4). v2: TranslationJob gains NextAttemptAt + LastErrorCode + an active-per-segment
    // unique index (M6). v3: TranslationJob gains SourceLanguage/TargetLanguage/PromptVersion and
    // MeetingSession gains a translation-direction snapshot (UI-R4A). v4: TranslationJob gains Model and
    // MeetingSession gains TranslationModel (UI-R4A fix). Migrations are explicit, transactional, and
    // preserve every existing row.
    public const int SchemaVersion = 4;

    public const string CreateSql = """
        CREATE TABLE IF NOT EXISTS MeetingSession (
            Id                  TEXT PRIMARY KEY,
            StartedAt           TEXT NOT NULL,
            EndedAt             TEXT,
            RecognitionLanguage TEXT NOT NULL,
            OutputDirectory     TEXT NOT NULL,
            RecordingPath       TEXT,
            State               TEXT NOT NULL,
            TranslationEnabled  INTEGER,
            TranslationSource   TEXT,
            TranslationTarget   TEXT,
            TranslationModel    TEXT,
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
            Id             TEXT PRIMARY KEY,
            SessionId      TEXT NOT NULL,
            SegmentId      TEXT NOT NULL,
            State          TEXT NOT NULL,
            AttemptCount   INTEGER NOT NULL DEFAULT 0,
            NextAttemptAt  TEXT,
            LastErrorCode  TEXT,
            LastError      TEXT,
            SourceLanguage TEXT NOT NULL DEFAULT 'ja',
            TargetLanguage TEXT NOT NULL DEFAULT 'zh',
            PromptVersion  INTEGER NOT NULL DEFAULT 1,
            Model          TEXT NOT NULL DEFAULT '',
            CreatedAt      TEXT NOT NULL,
            UpdatedAt      TEXT NOT NULL,
            FOREIGN KEY (SessionId) REFERENCES MeetingSession(Id),
            FOREIGN KEY (SegmentId) REFERENCES TranscriptSegment(Id)
        );

        CREATE INDEX IF NOT EXISTS IX_Session_State ON MeetingSession (State);

        CREATE UNIQUE INDEX IF NOT EXISTS IX_TranslationJob_ActiveSegment
            ON TranslationJob (SegmentId)
            WHERE State IN ('Pending','InProgress','RetryScheduled');
        """;

    /// <summary>Explicit v1 → v2 migration (adds retry columns + active-per-segment index; keeps data).</summary>
    public const string MigrateV1ToV2Sql = """
        ALTER TABLE TranslationJob ADD COLUMN NextAttemptAt TEXT;
        ALTER TABLE TranslationJob ADD COLUMN LastErrorCode TEXT;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_TranslationJob_ActiveSegment
            ON TranslationJob (SegmentId)
            WHERE State IN ('Pending','InProgress','RetryScheduled');
        """;

    /// <summary>
    /// Explicit v2 → v3 migration (UI-R4A): adds the translation-direction snapshot. Existing jobs
    /// default to ja→zh / prompt v1 (the only direction before this version); existing sessions get
    /// NULL translation columns (treated as legacy by the exporter). No row is deleted.
    /// </summary>
    public const string MigrateV2ToV3Sql = """
        ALTER TABLE TranslationJob ADD COLUMN SourceLanguage TEXT NOT NULL DEFAULT 'ja';
        ALTER TABLE TranslationJob ADD COLUMN TargetLanguage TEXT NOT NULL DEFAULT 'zh';
        ALTER TABLE TranslationJob ADD COLUMN PromptVersion INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE MeetingSession ADD COLUMN TranslationEnabled INTEGER;
        ALTER TABLE MeetingSession ADD COLUMN TranslationSource TEXT;
        ALTER TABLE MeetingSession ADD COLUMN TranslationTarget TEXT;
        """;

    /// <summary>
    /// Explicit v3 → v4 migration (UI-R4A fix): snapshots the model too. Existing jobs get an empty
    /// Model (the queue then falls back to the current model with a sanitized warning); existing
    /// sessions get a NULL TranslationModel. No row is deleted.
    /// </summary>
    public const string MigrateV3ToV4Sql = """
        ALTER TABLE TranslationJob ADD COLUMN Model TEXT NOT NULL DEFAULT '';
        ALTER TABLE MeetingSession ADD COLUMN TranslationModel TEXT;
        """;
}
