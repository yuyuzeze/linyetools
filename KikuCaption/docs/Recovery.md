# 异常恢复（Milestone 4）

`SessionRecoveryService.RecoverAsync` 在应用启动时运行（主窗口 `OnLoaded`，环境检查之后）。

## 流程

1. `store.InitializeAsync`：若数据库损坏则抛 `StorageException`，**恢复报错、不谎称成功**。
2. 查询未完成会话：`State NOT IN ('Completed','Recovered')`（即 `Running` / `StoppedDiskFull`）。
3. 对每个会话（相互隔离，单个失败不影响其它）：
   - 确保输出目录存在；
   - 删除残留 `*.tmp`（中断的原子写临时文件）；
   - 若 `transcript.json` / `session.json` 无法解析 → 改名备份为 `*.corrupt-<时间>.bak`（**不静默覆盖唯一副本**）；
   - 从 SQLite 重新导出 `transcript.json/.txt/.srt/session.json`；
   - 标记会话为 `Recovered`。
4. 返回 `RecoveryResult { RecoveredCount, FailedCount, Notes }`；主窗口顶部显示结果或错误。

## 关键性质

- **幂等**：重导出由 SQLite 决定，确定且无重复；标记 `Recovered` 后下次启动不再重复恢复。
- **不丢不重**：字幕来源于 SQLite 已提交的 final，按 `SequenceNumber` 稳定排序。
- **不删用户数据**：只删自己写的 `*.tmp`、只对损坏文件改名备份；从不删除无法识别的用户文件。
- **不伪装完成**：恢复只把会话标记为 `Recovered`，不会把未结束会话标成 `Completed`。
- **DB 损坏**：立即报错，用户仍可查看其它正常会话目录中的既有文件。

## 翻译任务恢复（Milestone 6）

翻译队列启动时（`TranslationQueue.StartAsync`，随应用启动）执行**幂等**恢复：

1. `RecoverInProgressJobsAsync`：把遗留 `InProgress`（上次运行崩溃于翻译途中）安全重置为 `Pending`。
2. `GetResumableJobsAsync`：把所有 `Pending` / `RetryScheduled`（到达 `NextAttemptAt` 后）重新排入队列继续翻译。

关键性质：**SQLite 是可靠待处理队列**——已入队任务在崩溃/重启后不丢；`Succeeded` 不重发；一个 segment 只有一个有效任务；
应用停止时取消当前 HTTP 请求但**保留 `Pending`**，下次启动继续；**翻译失败从不修改/删除原文 final**。
自动化见 `TranslationQueueTests`（真实 SQLite：恢复 Pending/RetryScheduled/InProgress、成功不重发、停止保留、原文不受影响）。

## 手工验证

1. 开始一次实时字幕会话，产生若干 final（此时 SQLite 已有记录，会话 `State=Running`）。
2. 强制结束 KikuCaption 进程（任务管理器结束进程）——请使用**虚构测试会话**，勿破坏真实数据。
3. 重新启动 KikuCaption：顶部应显示「已恢复 N 个会话」，该会话目录中的 `transcript.*` / `session.json`
   被重建且与 SQLite 一致；重复重启不产生重复字幕。

> 自动化：`SessionRecoveryServiceTests`（真实 SQLite）覆盖发现/重建/幂等/缺文件/损坏 JSON/残留 tmp/
> 空会话/DB 损坏/单会话失败隔离。
