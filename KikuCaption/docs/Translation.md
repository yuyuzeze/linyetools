# 翻译（Milestone 6：公司 Azure OpenAI 兼容 API 日译中）

当识别语言为**日语**且翻译功能启用时，程序把**已确认的 final 字幕文本**异步发送到公司内部
OpenAI 兼容 API，翻译成中文并显示、保存。**不上传音频、partial、PCM 或视频**（见文末「不会上传哪些数据」）。

模块隔离：`KikuCaption.Translation` 仅依赖 `Core`（不依赖 WPF/Storage）；App 负责组合，Storage 负责持久化。

## 公司 API 配置（全部可配置，不硬编码微软域名/deployment/api-version）

`appsettings.json` 的 `Translation` 段（示例，**绝不包含 ApiKey**）：

```json
"Translation": {
  "Enabled": false,
  "Endpoint": "",
  "Model": "",
  "ApiVersion": "",
  "AuthenticationMode": "Bearer",
  "HeaderName": "Authorization",
  "TimeoutSeconds": 30,
  "MaxRetries": 3,
  "MaxQueueLength": 100,
  "MaxConcurrency": 1,
  "MaxInputCharacters": 4000,
  "SourceLanguage": "ja",
  "TargetLanguage": "zh"
}
```

- `Endpoint`：**完整请求地址**。`ApiVersion` 非空时追加 `?api-version=<v>`（兼容 Azure 风格），为空则原样 POST（兼容纯 OpenAI 网关）。
- `Endpoint` 必须为 **HTTPS**，否则拒绝（`InvalidConfig`）。
- `Model`：模型或 deployment 名，写入请求体 `model` 字段。
- 主窗口「日译中翻译」面板可在运行时修改 Endpoint/Model/ApiVersion/认证模式/Header 名/超时/启用开关（`MaxQueueLength`/`MaxConcurrency` 在启动时读取，改动需重启）。

## 认证模式

| 模式 | 行为 |
|---|---|
| `Bearer` | `Authorization: Bearer <secret>` |
| `ApiKeyHeader` | 可配置 Header（默认 `api-key: <secret>`） |
| `None` | 不加客户端认证（公司网关已认证，或受控测试） |

密钥在**每次请求时**从 DPAPI 存储读取，**绝不进入日志/配置/SQLite/请求体之外**。

## 请求格式（标准 OpenAI Chat Completions 适配器）

协议细节全部隔离在 [`OpenAiCompatibleTranslationAdapter`](../src/KikuCaption.Translation/OpenAiCompatibleTranslationAdapter.cs)：

```
POST {Endpoint}[?api-version=...]
Content-Type: application/json
{认证头}
{
  "model": "<Model>",
  "messages": [
    { "role": "system", "content": "<固定 Prompt>" },
    { "role": "user",   "content": "<单条 ja final 原文>" }
  ],
  "temperature": 0.2, "top_p": 0.9, "max_tokens": <按输入长度上界>, "stream": false
}
```

- system 与原文**分离在不同 message**；原文**绝不拼进 system prompt**。
- 低随机性（temperature 0.2）；对输出做去空白、非空、长度、大小与 JSON 结构校验；空/非 JSON/错误 HTML/超大响应一律视为失败。
- 输入超过 `MaxInputCharacters` 直接拒绝（`InputTooLong`），不发送。
- 若公司真实 API 格式明显不同：**另提新的 Adapter 方案**，不大改现有模块。

固定系统 Prompt 见 [`TranslationPrompt`](../src/KikuCaption.Translation/TranslationPrompt.cs)（日中会议实时翻译助手，不总结/解释/扩写，保留人名与技术词，只输出中文）。

## 触发规则（同时满足才入队）

字幕为 final、源语言为 `ja`、翻译已启用、文本非空、该 segment 尚未成功翻译、不存在同一 segment 的活动任务。
**明确禁止**：翻译 partial、`zh` 识别模式调用日译中、每次 partial 变化都请求、同一 final 重复请求、上传音频/PCM/视频、一次性整场历史。

## 队列与并发

日语 final →（立即保存原文 + UI 立即显示原文）→ 写入 `TranslationJob(Pending)`（**SQLite 为可靠待处理队列**）→
有界 `Channel`（容量 `MaxQueueLength`）+ 周期 pump → 后台 worker（`MaxConcurrency`，默认 **1**，保序防限流）→
公司 API → 保存 Translation + 按 `SegmentId` 原地更新 UI 卡片 + 刷新 `translation.srt`。

- 不阻塞 Whisper / WASAPI / 录屏 / 原文写入 / WPF UI 线程。
- 队列满时**不丢任务**：durable 的 Pending 行由 pump 兜底恢复。
- 乱序返回按 `SegmentId` 更新正确卡片，不产生重复卡片。

## 重试与错误分类

| 分类 | 触发 | 策略 |
|---|---|---|
| `Timeout` / `Network` / `ServiceUnavailable`(5xx) / `RateLimited`(429) | 超时、网络暂时错误、5xx、429 | **重试**：指数退避 + jitter，≤ `MaxRetries`，429 遵循 `Retry-After`；超限 → `FailedPermanent` |
| `BadRequest`(400) / `Auth`(401/403) / `InvalidConfig`(无效模型/配置) | 明确不可重试 | **不重试** → `FailedPermanent` |
| `Cancelled` | 停止应用 | 当前请求取消，任务保留为 `Pending`（下次恢复） |

UI 提示：401/403 显示「认证失败」（不显示密钥）；429 显示限流与下次重试；5xx 显示服务暂时不可用；网络断开时原文与录屏继续。

## TranslationJob 状态（SQLite，schema v2）

`Pending / InProgress / Succeeded / RetryScheduled / FailedPermanent / Cancelled`。
记录 `JobId / SessionId / SegmentId / State / AttemptCount / NextAttemptAt / LastErrorCode / CreatedAt / UpdatedAt`。
**不保存**：API Key、Authorization Header、完整请求正文、完整响应、完整字幕。`LastErrorCode` 只存脱敏错误类别。

- 每 segment 只有一个有效任务（`Pending/InProgress/RetryScheduled` 上的部分唯一索引）。
- 成功不重发；崩溃后恢复 `Pending`/`RetryScheduled`；遗留 `InProgress` 幂等恢复为 `Pending`。
- **翻译失败不修改/删除原文 final**（原文 `Text` 从不改动，仅 `Status→TranslationFailed`）。

schema 迁移 v1→v2（新增 `NextAttemptAt`、`LastErrorCode` 列 + 活动唯一索引）采用显式 `user_version` 事务迁移，**保留旧数据、失败不静默重建**。详见 [Storage.md](Storage.md)。

## UI

主窗口「日译中翻译」面板：启用开关、Endpoint、Model/Deployment、API Version、认证模式、Header 名、API Key 安全输入
（`PasswordBox`）、保存密钥、清除密钥、**测试连接**、翻译状态、队列长度、最近错误。

- **测试连接**：发送固定非敏感文本 `テスト接続`（**绝不发送真实会议字幕**），明确显示成功/失败，不写日志、不回显密钥。
- 字幕显示：原文 final 立即显示；未返回时显小「翻译中…」；返回后**原地更新同一卡片**（浮窗双行 / 时间线双行），
  **不新增重复卡片、不强制滚动到底**；失败保留原文并显示低干扰「翻译失败」；`zh` 识别模式不显示翻译区。

## translation.srt 与存储

成功后更新 `TranscriptSegment.Translation` + `Status=Translated`、`TranslationJob=Succeeded`、`transcript.json`（含原文+译文）、
`translation.srt`、`session.json` 的 `translatedCount`。

`translation.srt`：使用原始 segment 的 StartTime/EndTime；序号连续；只输出翻译成功且中文非空的 segment；UTF-8；
时间格式 `HH:mm:ss,fff`；顺序与原文一致、不因 API 返回顺序打乱；重复导出不重复；应用重启后可由 SQLite 重建。
原文文件（transcript.srt/txt/json）继续保存原文，翻译失败不修改原文。

## 离线与错误行为

- 翻译关闭 / 无 Key / 网络断开 / 认证失败：**原始字幕与录屏照常运行**，不阻塞、不丢原文。
- 应用停止：取消当前 HTTP 请求，`Pending` 任务保留，下次启动自动恢复继续。

## 真实公司 API 验证步骤（需用户提供**非敏感**信息）

1. 在面板填写 **Endpoint**、**Model/Deployment**、**ApiVersion**（如需要）、**认证模式 + Header 名**。
2. 在 `PasswordBox` 输入 **API Key** 并点「保存密钥」（DPAPI 加密到本机，**不要贴进代码或聊天**）。
3. 点「测试连接」（固定文本 `テスト接続`）确认连通与认证。
4. 用一条非敏感日语测试句（如「今日の会議を始めます」）走完整流程，确认原地出现中文、`translation.srt` 生成。
5. 若真实 API 格式与标准 OpenAI Chat Completions 明显不同，请提供**一个请求/响应示例**，我据此新增 Adapter。

在用户提供上述配置前，真实公司 API 结果标记为**未验证**（fake 端到端已通过，见 [Verification.md](Verification.md)）。

## 不会上传哪些数据

- ❌ 会议音频、WASAPI PCM、录屏视频、MP4
- ❌ partial 字幕
- ❌ 一次性整场历史字幕
- ❌ API Key / Authorization Header（不入日志/正文之外）
- ✅ 仅**逐条**日语 **final** 文本（启用翻译时）随固定 Prompt 发送
