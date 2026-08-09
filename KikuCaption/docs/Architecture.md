# KikuCaption 架构说明

本文件随里程碑推进增量更新。当前反映 **Milestone 0** 的骨架。

## 项目与依赖方向

```text
        KikuCaption.App (WPF, net10.0-windows)
              │  组合根：Generic Host + DI + Serilog + MVVM
              ├───────────────┐
              ▼               ▼
   KikuCaption.Infrastructure   KikuCaption.Core (net10.0)
   (net10.0)                    领域模型 / 枚举 / 接口
              │                 —— 不依赖任何外部实现
              ▼
        KikuCaption.Core
```

- `Core` 不依赖 WPF、NAudio、FFmpeg、SQLite 或 HTTP 实现（PROJECT.md 7）。
- `Infrastructure` 仅依赖 `Core`，承载配置校验、日志、环境检查、子进程调用等横切关注点。
- `App` 作为组合根，把接口绑定到实现，负责 UI 与生命周期。
- 无项目间循环引用。

## Milestone 0 已建立的构件

### Core

- 模型：`AudioChunk`、`TranscriptSegment`、`MeetingSession`（PROJECT.md 8.1）。
- 枚举：`TranscriptStatus`、`EnvironmentCheckStatus`、`DependencyKind`。
- 环境检查契约：`IEnvironmentChecker`、`EnvironmentReport`、`DependencyCheckResult`。

> 领域服务接口（`IAudioCaptureService`、`IScreenRecorder`、`ISpeechRecognizer`、
> `ITranscriptStabilizer`、`ITranscriptRepository`、`ITranscriptExporter`、
> `IAiTranslationService`、`ITranslationQueue`）及其配套 DTO 将在各自 Milestone 引入。
> 现在不提前声明，是为遵守 PROJECT.md 18.1/18.4（只实现当前范围、避免空接口填充）。

### Infrastructure

- `Configuration`：`KikuCaptionOptions` 及各 `*Settings`，`KikuCaptionOptionsValidator`
  （启动时校验，PROJECT.md 11）。
- `Logging`：`SerilogConfigurator` —— `logs/app-yyyyMMdd.log` 按天滚动（PROJECT.md 15）。
- `Processes`：`IProcessRunner` / `ProcessRunner` —— 用结构化参数列表启动子进程，
  避免命令注入（PROJECT.md 13），可执行文件缺失时返回 `NotFound` 而非抛异常。
- `Diagnostics`：`IEnvironmentProbe` 与四个探针
  （`DotNetRuntimeProbe`、`PythonProbe`、`FFmpegProbe`、`DiskSpaceProbe`），
  由 `EnvironmentChecker` 聚合；单个探针失败被隔离为 Error 结果，不影响整体报告。

### App

- `App.xaml.cs`：构建 Generic Host，注入 Infrastructure，配置 Serilog，启动时强制校验配置，
  再显示主窗口；任何启动异常以对话框呈现，不崩溃。
- `MainViewModel`（CommunityToolkit.Mvvm）：异步运行环境检查，UI 不阻塞。
- `MainWindow`：展示各依赖状态、版本、说明与补救建议。

## Milestone 1（已实现）：KikuCaption.Audio

`net10.0-windows`，引用 `Core`；**不引用 WPF/WinForms**（音频模块与 UI 解耦）。

```text
WPF (AudioCaptureViewModel)  ──调用──►  ISystemAudioWavRecorder
                                              │  (单个后台 pump 任务)
                                              ▼
                                       IAudioCaptureService  (KikuCaption.Core 契约)
                                              │  = WasapiLoopbackAudioCaptureService
              WASAPI DataAvailable ──►  AudioFormatConverter ──►  BoundedAudioBuffer(Channel<AudioChunk>)
              (设备线程, 任意格式)        (下混+重采样→16k/mono/int16)     (有界, TryWrite 满则记丢帧)
                                              │
                                        await foreach 消费 ──►  WavFileWriter (16k/mono/16-bit)
```

- **Conversion/**：`AudioFormatConverter`（`WdlResamplingSampleProvider` + `MonoDownmixSampleProvider`，
  流式、保持重采样滤波状态）。
- **Buffering/**：`BoundedAudioBuffer`（有界 `Channel<AudioChunk>`；`TryWrite` 非阻塞，满则计入
  `DroppedChunkCount`——背压指标，绝不无界增长，PROJECT.md 6）。
- **Capture/**：`WasapiLoopbackAudioCaptureService : IAudioCaptureService`（状态机
  Idle→Capturing→Stopped/Disposed；设备断开经 `RecordingStopped.Exception` 转为
  `AudioCaptureException`；取消/重复启动/释放安全）；`SystemAudioWavRecorder`（Start/Stop/状态/指标/
  Faulted 事件，单个 `Task.Run` pump）。
- **Wav/**：`WavFileWriter`（NAudio `WaveFileWriter`，收尾写入 RIFF 头长度）。
- **DependencyInjection/**：`AddKikuCaptionAudio()`。

依赖方向保持 `App → Audio → Core`；UI 不含任何 WASAPI/转换逻辑。

## Milestone 2（已实现）：KikuCaption.Speech + python/whisper_worker

`net10.0-windows`，引用 `Core`；不引用 WPF。C# 通过 JSON Lines 驱动常驻 Python worker。

```text
WPF (SpeechViewModel)  ──►  ISpeechRecognizer (Core 契约)
                               = PythonSpeechRecognizer
   InitializeAsync ─ initialize ─►┐        单一 stdout 读取循环 ◄─ ready/partial/final/flushed/error
   RecognizeAsync  ─ audio… flush ►│                 │
        （逐块，管道背压）          ▼                 ▼
                          ProcessWhisperWorker  ──►  JsonLinesChannel(有界队列, 串行 stdin)
                               │  (Job Object 防孤儿, 独立 stderr 读取)
                               ▼  stdin/stdout JSON Lines
                          python main.py  ─► protocol.py / streaming.py / recognizer.py
                               │
                          faster-whisper small/int8/cpu（进程内加载一次）
```

- **Core**：`ISpeechRecognizer`、`SpeechOptions`、`TranscriptUpdate`(+`TranscriptUpdateKind`)、`SpeechRecognitionException`。
- **Speech/Protocol**：`ProtocolMessage`、`JsonLinesCodec`（序列化+校验）、`ProtocolConstants`、`ProtocolException`。
- **Speech/Worker**：`IWhisperWorker`、`ProcessWhisperWorker`（进程+Job Object+独立 stderr）、`JsonLinesChannel`
  （单读 stdout / 串行 stdin / 有界背压）、`WindowsJobObject`、`PythonSpeechRecognizer`、`WhisperWorkerLocator`。
- **Python worker**：`protocol.py`（校验）、`recognizer.py`（加载一次模型）、`streaming.py`（缓冲+转写，M2 基础）、`main.py`。
- **Audio**：新增 `WavFileAudioReader`（WAV→16k/mono/int16 AudioChunk，供 WAV 识别）。

模型缓存目录可配置（`Speech:ModelCacheDirectory`），默认 `<repo>/models/whisper`。**不含** Stable Prefix/Finalizer/Overlay/SQLite/翻译/录屏（后续 Milestone）。

## Milestone 3（已实现）：渐进字幕与字幕浮窗

**数据流**：`WASAPI → AudioChunk(M1) → RealtimeCaptionPipeline → 每周期用 M2 ISpeechRecognizer 转写当前语句 → 候选 → TranscriptStabilizer + Finalizer → partial/final 事件 → SubtitleOverlay + 主窗口`。

```text
IAudioCaptureService(M1) ──► RealtimeCaptionPipeline（KikuCaption.Speech.Streaming）
   滚动语句缓冲(有界) + 能量 VAD        │  单一在途转写(不并发破坏 worker 时序)；落后实时→背压跳过计数
                                        ▼
   每 PartialIntervalMs: ISpeechRecognizer.RecognizeAsync(当前语句)  ──►  M2 worker（模型只加载一次）
                                        │  候选文本(增长)
                                        ▼
   TranscriptStabilizer（LocalAgreement，按 Unicode 码点，CJK 友好，committed 单调不回退）
                                        │  StableText / PartialText
                                        ▼
   Finalizer（静音/句末标点+稳定/最大句长/最大等待/Flush）  ──►  final 不可变
                                        │  PartialUpdated / FinalProduced / Faulted 事件（后台线程）
                                        ▼
   RealtimeCaptionViewModel（Dispatcher 编组）──► SubtitleOverlayViewModel ──► SubtitleOverlayWindow
```

- **Core**：`ITranscriptStabilizer`、`StabilizationResult`。
- **Speech.Stabilization**：`TranscriptStabilizer`、`Finalizer`、`ProgressiveCaptionOptions`（范围校验）、`CaptionText`（CJK 前缀/标点工具）。
- **Speech.Streaming**：`RealtimeCaptionPipeline`（有界、单在途、指标、生命周期）、`CaptionEvents`。
- **App**：`RealtimeCaptionViewModel`、`SubtitleOverlayViewModel`、`CaptionLineViewModel`、`Views/SubtitleOverlayWindow`（置顶/拖动/鼠标穿透 P/Invoke/不抢焦点；算法不在 code-behind）。

**复用 M2**：M3 未改协议/worker——每周期通过 `RecognizeAsync`（audio+flush）转写当前语句，纯 C# 编排（属 M3 内部实现）。**不含** SQLite/持久化/FFmpeg/翻译。

## Milestone 4（已实现）：KikuCaption.Storage

`net10.0`，依赖 `Core`，不依赖 WPF。`Microsoft.Data.Sqlite`（原生库固定为已修补的 e_sqlite3 3.50.3）。

```text
RealtimeCaptionPipeline.FinalProduced ──► RealtimeCaptionViewModel(OnFinalPersist)
                                              │ 构造 Final TranscriptSegment
                                              ▼
                                        SessionRecorder（有界队列, 背压不丢弃）
                                     ┌────────┴─────────┐
                                     ▼                  ▼ (去抖 ~1s / 停止)
                        SqliteTranscriptRepository   TranscriptExporter（原子写）
                        （final 立即提交, 幂等 upsert） json/txt/srt/session.json
                                     ▲
应用启动 ─► SessionRecoveryService ──┘（扫描未完成会话 → 从 SQLite 重建文件 → 标记 Recovered）
```

- **Core**：`ITranscriptRepository`、`ITranscriptExporter`、`StorageException`。
- **Storage/Sqlite**：`SqliteTranscriptRepository`(实现 `ITranscriptStore`：写 + 读)、`SqliteSchema`(user_version=1)、`StoredRecords`。
- **Storage/Export**：`TranscriptExporter`（JSON/TXT/SRT/session.json）、`AtomicFile`（临时文件+替换）。
- **Storage**：`SessionRecorder`（实时持久化管线）、`SessionPaths`（目录名+路径穿越守卫）、`DiskSpace`、`StorageOptions`。
- **Storage/Recovery**：`SessionRecoveryService`。
- **App**：`RealtimeCaptionViewModel` 连接 final→recorder 并展示存储状态；`MainViewModel.RunRecoveryAsync` 启动时恢复。

依赖方向 `App → Storage → Core`；`Core` 不依赖 SQLite。详见 [Storage.md](Storage.md) 与 [Recovery.md](Recovery.md)。**不含** FFmpeg/录屏/MP4/翻译。

## Milestone 5（已实现）：KikuCaption.Recording

`net10.0-windows`，依赖 `Core`，不依赖 WPF。以结构化参数管理 FFmpeg 子进程。详见 [Recording.md](Recording.md) / [FFmpeg.md](FFmpeg.md)。

```text
                         RealtimeCaptionViewModel.StartRecordingAsync
                                     │  (能力探测→编码器；会话目录/meeting.mp4)
                                     ▼
IAudioCaptureService(M1, 第二路) ─► FFmpegScreenRecorder (IScreenRecorder)
   16k/mono/int16 (背压隔离)         │  gdigrab 视频 + 命名管道音频 + Job Object 防孤儿
                                     ├─► NamedPipeAudioSink(有界队列, 当前用户 ACL) ─► FFmpeg stdin? no: \\.\pipe\<guid>
                                     ▼
                              ffmpeg.exe: gdigrab(screen/window) + s16le pipe → H.264(qsv/libx264)+AAC → meeting.mp4
                                     │  停止: stdin 'q' → 超时 kill-tree → ffprobe 校验
                                     ▼
                          RecordingResult (仅可播放才 IsComplete) → 更新 M4 RecordingPath/session.json
```

- **Core**：`IScreenRecorder`、`RecordingOptions`、`RecordingResult`、枚举 `RecorderState`/`CaptureTargetType`、`RecordingException`。
- **Recording/FFmpeg**：`FFmpegLocator`、`FFmpegArgumentBuilder`(纯)、`FFmpegCapabilityProbe`(真实 QSV 短编码)、`FFprobe`。
- **Recording/CaptureTargets**：`WindowEnumerator`(user32)、`CaptureTarget`。
- **Recording/Muxing**：`NamedPipeAudioSink`(唯一名+ACL+有界+丢帧计数)。
- **Recording/Processes**：`ProcessRunner`、`WindowsJobObject`。
- **Recording**：`FFmpegScreenRecorder`(状态机/优雅停止/ffprobe 校验/防孤儿)、DI。
- **App**：`RealtimeCaptionViewModel` 协调录制与字幕/存储，捕获目标 UI；`RecordingRuntimeOptions` 承载定位到的 FFmpeg 与编码器偏好。

依赖 `App → Recording → Core`。**不含** Azure/翻译/translation.srt。

## Milestone 6（已实现）：KikuCaption.Translation（公司 OpenAI 兼容日译中）

新增 `KikuCaption.Translation`（net10.0-windows，**仅依赖 Core**，不依赖 WPF/Storage）。依赖方向
`App → Translation → Core`；持久化契约 `ITranslationJobStore`（Core）由 `KikuCaption.Storage` 实现，
故 Translation 不引用 Storage，**无循环**。

```
日语 final(M3) ──► 立即保存原文(M4) + UI 立即显示原文(M3/M3.1)
                        │
                        ▼  TranslationQueue.EnqueueAsync（触发规则）
             TranslationJob(Pending) 写入 SQLite（可靠待处理队列）
                        │  有界 Channel + 周期 pump
                        ▼
             worker（MaxConcurrency，默认1）
                        ▼  OpenAiCompatibleTranslationAdapter（IHttpClientFactory）
             公司 OpenAI 兼容 API（Bearer/ApiKeyHeader/None；DPAPI 读取密钥）
                        ▼
   SetSegmentTranslation(Translated) + Job=Succeeded
                        ▼  OutcomeChanged（按 SegmentId，UI 线程）
   时间线/浮窗原地双行更新 + 重导出 translation.srt（M4 导出器）
```

- 组件：[`OpenAiCompatibleTranslationAdapter`](../src/KikuCaption.Translation/OpenAiCompatibleTranslationAdapter.cs)（协议隔离）、
  [`TranslationQueue`](../src/KikuCaption.Translation/TranslationQueue.cs)（有界队列+重试+恢复）、
  [`DpapiTranslationSecretStore`](../src/KikuCaption.Translation/Security/DpapiTranslationSecretStore.cs)（密钥）、
  `TranslationTrigger` / `TranslationBackoff` / `TranslationErrorClassifier`（纯逻辑）。
- 故障隔离：翻译失败/断网/无 Key **不影响原文与录屏**；密钥经 DPAPI，绝不入日志/配置/SQLite。
- 详见 [Translation.md](Translation.md)、[Security.md](Security.md)。

## Milestone 7（已实现）：集成、稳定性与交付

统一会话生命周期与稳定性/交付设施，不新增录制/翻译功能。

- **状态机**（Core）：[`SessionStateMachine`](../src/KikuCaption.Core/Session/SessionStateMachine.cs) +
  `SessionState`（Idle/Preflight/Starting/Running/Stopping/Completed/Faulted/Recovering）。纯、线程安全、可单测：
  合法转移、拒绝重复开始、幂等停止、启动失败回滚、故障隔离。`RealtimeCaptionViewModel` 驱动它统一单一「开始/停止」。
- **预检**（Core 纯评估 + App 采集）：[`PreflightEvaluator`](../src/KikuCaption.Core/Session/Preflight.cs) 把
  facts 归类为 通过/警告/阻断（音频/模型/存储/输出/磁盘=阻断；录屏=非静默警告需选择；翻译=警告仅原文）；
  [`PreflightService`](../src/KikuCaption.App/Services/PreflightService.cs) 采集真实事实。
- **资源监控**（Infrastructure）：[`CpuUsageCalculator`](../src/KikuCaption.Infrastructure/Diagnostics/CpuUsageCalculator.cs) +
  `ProcessCpuSampler`（`TotalProcessorTime` 增量 ÷ elapsed×核数，退出不写 0%）+ `DiagnosticsSnapshot`/`DiagnosticsFormatter`（仅数值，脱敏）。
- **日志/隐私**：`LogRetention`（启动清理超期）+ `SensitiveInfoScanner`（源码/配置/发布包/日志/DB 扫明文密钥）。
- **设置**：`UserSettingsStore`（用户可写目录 JSON，损坏回退安全默认+备份，**不存密钥**）。
- **交付**：`scripts/publish.ps1`（自包含 win-x64 + 排除规则 + zip + SHA-256）、`scripts/setup-python.ps1`、
  `THIRD_PARTY_NOTICES.md` + `licenses/`。方案 A（自包含 .NET + 脚本化 Python）。详见 [Delivery.md](Delivery.md)。

依赖方向不变：App 组合各层；Core 不依赖任何上层；新增诊断/设置在 Infrastructure，状态机/预检在 Core。
