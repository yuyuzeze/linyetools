# C# ↔ Python Whisper Worker 协议

> 状态：**已在 Milestone 2 实现**。C# 端 `KikuCaption.Speech.Protocol`，Python 端
> `python/whisper_worker/protocol.py`，协议版本 **v=1**（依据 PROJECT.md 8.3、13）。

## 传输

- C# 以长期驻留子进程方式启动 Python Worker（`ProcessWhisperWorker`）。
- V1 使用 **stdin/stdout 上的 JSON Lines**（每行一个紧凑 JSON 对象）。
- `stdout` 只输出协议消息；一切诊断/日志写入 `stderr`（C# 端由独立循环读取，见并发约束）。
- 单条音频消息上限 **`MAX_AUDIO_BYTES = 16000×2×10`（10 s）**，两端都校验。

## 通用字段（每条消息）

| 字段 | 类型 | 说明 |
|---|---|---|
| `v` | int | 协议版本，必须为 1，否则回 `error(version_mismatch)` |
| `type` | string | 消息类型 |
| `sessionId` | string | 会话 GUID |
| `seq` | int | 发送方单调递增序号 |

## 消息类型

| 方向 | type | 关键字段 |
|---|---|---|
| C#→Py | `initialize` | `model`,`device`,`computeType`,`beamSize`,`language`(ja/zh),`modelCacheDir?` |
| Py→C# | `ready` | `modelLoadMs`,`model`,`device`,`computeType` |
| C#→Py | `audio` | `pcm`(Base64 int16 LE mono 16k),`frames` |
| Py→C# | `partial` | `start`,`end`,`text` |
| Py→C# | `final_candidate` | `start`,`end`,`text`,`confidence?` |
| C#→Py | `flush` | — |
| Py→C# | `flushed` | `count`（本批 final 段数，作为完成标记） |
| Py→C# | `error` | `code`,`message` |
| C#→Py | `shutdown` | — |

Milestone 2 的 `flush` 语义：Worker 对已缓冲音频转写，先对每段发 `partial`，随后对每段发
`final_candidate`，最后发 `flushed`。滑动窗口 / Stable Prefix / Finalizer 属 **Milestone 3**，此处未实现。

## 校验（不可信输入边界，PROJECT.md 13）

- `v` 必须匹配；`type` 必须已知；`sessionId`/`seq` 必填。
- `audio.pcm` 必须是合法 Base64；解码后长度必须为偶数（int16）、非空、且 ≤ `MAX_AUDIO_BYTES`；
  若带 `frames` 则须与 `len(pcm)/2` 一致。
- Worker 收到非法消息时回结构化 `error`，**绝不把 traceback 打到 stdout**。
- C# 端发送前也用 `JsonLinesCodec.CreateAudio` 校验 PCM 长度/大小。

## 并发与背压（PROJECT.md M2）

- **单一 stdout 读取者**：`JsonLinesChannel` 的一个后台循环读 stdout → 有界 `Channel<ProtocolMessage>`。
- **stdin 写入串行化**：`SemaphoreSlim` 保证并发发送不会交叉一行 JSON。
- **有界队列**：入站消息通道有界（默认 256），消费慢时经 OS 管道对 Worker 施加背压，不无限增长。
- **音频不无界累积**：C# 逐块发送并 `await`，管道满则等待，Base64 不在内存堆积。
- `stderr` 由 `ProcessWhisperWorker` 独立循环读取并保留最近 200 行（不含 PCM）。

## 生命周期与防孤儿

- 关闭时先发 `shutdown`，关闭 stdin（EOF），等待 `ShutdownTimeout`，超时才 `Kill(entireProcessTree)`。
- 子进程被分配到 **Windows Job Object（kill-on-close）**：即使父进程崩溃，Worker 也会被终止，避免孤儿。
- Worker 崩溃/退出：读取循环结束 → 置 `worker_exited` 故障 → 挂起的初始化/识别快速失败，不使 WPF 崩溃。

## Milestone 3 说明（协议未变更）

M3 的渐进字幕**复用本协议，未新增或修改任何消息**：`RealtimeCaptionPipeline` 每个 partial 周期通过
`ISpeechRecognizer.RecognizeAsync`（即 `audio…` + `flush`）转写“当前语句”音频，得到 worker 的
`final_candidate` 作为**候选**；随后由 C# 侧 `TranscriptStabilizer`（稳定前缀）与 `Finalizer`
决定 partial/final。滑动窗口、Stable Prefix、Finalizer 全部在 `KikuCaption.Speech`（非 worker、非 UI）。
