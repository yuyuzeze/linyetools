# 存储设计（Milestone 4）

> `KikuCaption.Storage`，依赖 `Core`，不依赖 WPF。`Microsoft.Data.Sqlite` +
> `System.Text.Json` + 内置文件 IO。

## 数据库位置

单个 SQLite 数据库：`<输出根>/kikucaption.db`。
输出根 = `Storage.OutputDirectory`（相对路径基于应用运行目录 `AppContext.BaseDirectory`）。
连接开启 `PRAGMA foreign_keys=ON` 与 `journal_mode=WAL`。

## Schema 版本

`PRAGMA user_version`；当前 `SchemaVersion = 2`（M6）。

- `user_version==0`：创建全部表（v2）并置为 2。
- `==1`：**显式事务迁移** v1→v2（见下），成功后置为 2；失败回滚并抛 `StorageException("migration_failed")`，**保留旧数据、不静默重建**。
- `==2`：直接使用。
- `>2`：拒绝打开（`StorageException("schema_newer")`），避免破坏更新版本的数据。
- 文件损坏/非数据库：`StorageException("db_init_failed")`，不静默重建。

### 迁移 v1 → v2（M6）

`TranslationJob` 增加 `NextAttemptAt`、`LastErrorCode` 两列，并建活动唯一索引
`IX_TranslationJob_ActiveSegment (SegmentId) WHERE State IN ('Pending','InProgress','RetryScheduled')`。
迁移在单个事务内 `ALTER TABLE ADD COLUMN` + 建索引，保留所有旧行。已在真实旧库上实测（应用启动日志：
“Migrated database schema v1 → v2”）。

## 表结构

`MeetingSession`：`Id(PK)`, `StartedAt`, `EndedAt`, `RecognitionLanguage`, `OutputDirectory`,
`RecordingPath`(空), `State`, `CreatedAt`, `UpdatedAt`。

`TranscriptSegment`：`Id(PK)`, `SessionId(FK)`, `SequenceNumber`, `StartTicks`, `EndTicks`,
`Language`, `Text`, `Translation`(空), `Status`, `Confidence`, `CreatedAt`, `UpdatedAt`；
唯一索引 `(SessionId, SequenceNumber)`。

`TranslationJob`：`Id(PK)`, `SessionId(FK)`, `SegmentId(FK)`, `State`, `AttemptCount`,
`NextAttemptAt`(M6), `LastErrorCode`(M6), `LastError`, `CreatedAt`, `UpdatedAt`。
状态 `Pending/InProgress/Succeeded/RetryScheduled/FailedPermanent/Cancelled`（M6 执行翻译，见 [Translation.md](Translation.md)）。
活动唯一索引保证**每 segment 只有一个有效任务**。`LastErrorCode` 只存脱敏错误类别；**绝不存** Key/Header/请求正文/完整响应/完整字幕。
翻译成功后仅更新 `TranscriptSegment.Translation` + `Status=Translated`；**原文 `Text` 从不因翻译失败被修改**。

索引：`(SessionId, SequenceNumber)`、`MeetingSession(State)`。时间以 `Ticks`（无损）、
时间戳以 ISO-8601(`"O"`) 存储。

## 领域模型映射

- `MeetingSession`(Core) ↔ `MeetingSession` 表；`State/CreatedAt/UpdatedAt` 由仓储管理。
- `TranscriptSegment`(Core) ↔ `TranscriptSegment` 表；`SequenceNumber` 首次插入按到达顺序赋值，
  幂等 upsert（按 `Id`）不改序号；**仅 `Status=Final` 落库**，partial 永不写。
- M3 `CaptionFinalEventArgs` + `pipeline.SessionId` + 所选语言 → 构造 `Final` 段 → `RecordFinalAsync`。

## 实时保存策略与延迟

`final → 有界队列(容量默认256, 满则背压不丢弃) → 后台写入 → SQLite 立即提交（每条 final 独立事务）
→ 标记脏 → 去抖(默认 1000 ms) 从 SQLite 重导出文件`。

- SQLite 中 final **立即存在**（UI 不等待磁盘）。
- 文件（JSON/TXT/SRT/session.json）**最大延迟 ≈ 去抖间隔**；停止时做最终导出。
- 写入失败：停止接收 + 置 `StorageError` + 抛 `StorageFailed` 事件，**不伪装成功**。
- 关闭：`StopSessionAsync` 先 drain 队列（不丢最后一条），再标记结束并最终导出。

## 文件格式

- `transcript.json`（UTF-8，缩进）：数组，元素含 `id, sessionId, sequenceNumber, start, end,
  language, text, translation, status, confidence, createdAt`；`start/end` 为秒(double)；顺序按
  `SequenceNumber`；原子写（临时文件 + 替换）。
- `transcript.txt`（UTF-8）：每行 `"[HH:mm:ss] 原文"`（固定格式），仅 final、无 partial、无重复。
- `transcript.srt`（UTF-8）：序号从 1 递增，时间 `HH:mm:ss,fff`，`Start ≤ End`（倒置则夹紧），
  保留中日文/换行，跳过空字幕。
- `session.json`（UTF-8）：`sessionId, startedAt, endedAt, recognitionLanguage, state,
  outputDirectory, recordingPath, segmentCount, appVersion, dataFormatVersion(=1)`。
  （`recordingPath` 为 Milestone 5 的 `meeting.mp4` 路径，未录屏时为空。）

## 磁盘空间

开始前 `DiskSpace.HasAtLeastGb(root, MinimumFreeSpaceGb)`，不足抛 `StorageException("insufficient_disk")`；
运行中每 ~500 ms 复查，跌破阈值 → 停止接收、标记 `StoppedDiskFull`、触发 `DiskLow` 事件（UI 据此安全停止）。

## 隐私

参数化 SQL；日志只记录 `SessionId/SegmentId/序号/文本长度`，**不记录字幕正文、partial、PCM、音频**。
路径统一 `Path.GetFullPath` 规范化并校验位于输出根内，拒绝路径穿越。
