# KikuCaption 项目需求与实施规范

> 文档状态：已确认的项目基线  
> 目标平台：Windows 11  
> 主技术栈：.NET 10、C#、WPF、Python faster-whisper、FFmpeg  
> 目标硬件：Intel Core i5-1335 系列、16 GB RAM、无独立显卡  
> 核心约束：应用及其运行依赖的总硬盘占用目标小于 10 GB

## 1. 项目概述

KikuCaption 是一个主要面向 Microsoft Teams 会议的个人 Windows 桌面工具。名称中的“Kiku”取自日语“聞く（听）”，表达听取会议内容并生成字幕的核心用途。它在本机录制屏幕和系统声音，使用本地 faster-whisper 对日语或中文进行实时语音识别，以渐进方式显示字幕，并持续保存原始字幕。

当识别语言为日语时，程序只把已经确认的 final 字幕文本发送给公司内部提供的 Azure OpenAI 兼容 API，翻译成中文。原始会议音频不因翻译功能上传到该 API。

项目采用按 Milestone 逐步交付的方式。每次只实施一个 Milestone，完成、验证并获得用户下一步指示后，才可继续。

## 2. 项目目标

### 2.1 核心功能

1. 在 Windows 11 上录制整个屏幕或指定窗口，主要目标是 Teams 会议画面。
2. 捕获 Windows 系统输出声音，主要目标是用户实际听到的 Teams 会议声音。
3. 将录制结果保存为带声音的 MP4 文件，并保持可接受的音画同步。
4. 在无独立显卡的设备上，以 CPU 运行 faster-whisper `small`、`int8` 模型。
5. 支持用户在会前明确选择日语 `ja` 或中文 `zh`；Auto 仅作为后续实验选项。
6. 以 partial 和 final 两种状态显示渐进式实时字幕。
7. 实时、增量地保存字幕，避免程序异常退出时丢失整场会议内容。
8. 保存结构化 JSON、纯文本 TXT、原文 SRT，以及存在翻译时的中文 SRT。
9. 当日语字幕成为 final 后，将文本异步发送给公司内部 Azure OpenAI 兼容 API，并显示、保存中文翻译。
10. 提供主控制窗口和可置顶的字幕浮窗。
11. 模块化设计，使录屏、音频、识别、字幕稳定化、翻译和存储可独立替换或测试。

### 2.2 资源目标

- 安装包、.NET/Python 运行依赖、FFmpeg、Whisper 模型、程序文件等静态内容合计目标：小于 10 GB。
- “小于 10 GB”指应用及依赖的硬盘占用，不包括用户长期积累的会议 MP4、字幕、日志和导出文件。
- 首选运行内存目标：小于 5 GB；硬限制：小于 10 GB。
- 实时字幕端到端目标延迟：通常 1～3 秒。
- 识别 Real-Time Factor（RTF）目标：小于 1.0。
- 会议录制默认 15 FPS，避免为会议场景消耗不必要的 CPU 和磁盘。

## 3. 非目标（V1 不做）

- 说话人分离或自动标注多人身份。
- 同一语句内复杂的中日自动切换识别。
- 云端账户、登录、多用户、权限系统或后台服务。
- Teams 插件或 Teams Tenant 集成。
- 云端同步、知识库、全文搜索和字幕时间轴跳转。
- 自动会议总结、待办事项提取、决定事项提取。
- 移动端或 macOS/Linux 客户端。
- 60 FPS、高码率或游戏级录屏。
- 将 partial 字幕发送到翻译 API。
- 为了规避公司政策而隐藏录音、录屏或转录行为。

## 4. 关键设计原则

1. **本地优先：** 音频采集、录制和语音识别均在本机完成。
2. **最少外发：** 只有日语 final 字幕文本可发送给公司内部翻译 API；音频和 partial 字幕不发送。
3. **管线解耦：** 使用有界 `Channel<T>` 和明确接口连接各模块，UI 不直接承担音频处理或识别算法。
4. **故障隔离：** 翻译、录屏或 Whisper Worker 的单点失败不得导致已识别原文丢失。
5. **实时落盘：** final 字幕产生后立即持久化，不等会议结束再一次性保存。
6. **可替换性：** 识别和翻译均通过接口抽象，未来可加入其他实现。
7. **小步交付：** 严格按 Milestone 实施，不一次生成全部功能。
8. **需求受控：** 实现者可提出优化建议，但未经用户确认不得修改本文件中的既定需求。

## 5. 技术栈与类库

### 5.1 主程序

- C# / .NET 10
- WPF
- MVVM
- `CommunityToolkit.Mvvm`：ViewModel、命令和属性通知
- `Microsoft.Extensions.DependencyInjection`：依赖注入
- `Microsoft.Extensions.Hosting`：应用生命周期和后台服务（仅桌面进程内，不引入 Web 服务）
- `Microsoft.Extensions.Configuration`：配置
- `Microsoft.Extensions.Logging`：统一日志抽象
- `Serilog.Extensions.Logging`、`Serilog.Sinks.File`：滚动文件日志（可选但推荐）
- `System.Threading.Channels`：有界异步处理管线
- `System.Text.Json`：JSON 与 JSON Lines 协议
- `IHttpClientFactory`：翻译 API HTTP 客户端管理

### 5.2 音频

- `NAudio`
- `WasapiLoopbackCapture`：系统输出音频捕获
- NAudio resampler：统一转换为 16 kHz、单声道、PCM signed 16-bit little-endian

识别端统一音频格式：

```text
Sample rate: 16000 Hz
Channels: 1 (mono)
Sample format: signed PCM int16 little-endian
```

### 5.3 录屏与封装

- FFmpeg 可执行文件，由 C# 作为长期子进程管理。
- V1 直接调用 FFmpeg 完成屏幕/窗口画面捕获与 H.264 编码，不在 .NET 内自行实现视频编码器。
- 默认优先尝试 Intel Quick Sync `h264_qsv`；能力检测失败时自动回退 `libx264`。
- V1 可使用 FFmpeg 的 Windows 捕获输入（例如 `gdigrab`）实现桌面或指定窗口录制。
- 系统音频由 NAudio WASAPI Loopback 捕获；同一份 PCM 同时用于识别和录制。
- 为避免依赖虚拟声卡，优先通过命名管道把 PCM 送入 FFmpeg，与视频实时复用为 MP4；如果该路径在目标环境不稳定，可在获得用户确认后改为先写临时 WAV、停止时再由 FFmpeg 无损复用，但必须保持实时字幕不受影响并记录崩溃恢复策略。

### 5.4 本地语音识别

- Python 3.11 或与所选依赖明确兼容的稳定版本
- `faster-whisper`
- `ctranslate2`
- faster-whisper 内置 Silero VAD；仅在确有需要时独立引入其他 VAD 包
- 默认模型：`small`
- 设备：`cpu`
- 计算类型：`int8`
- 默认 `beam_size=1`
- 模型在 Worker 启动时加载一次，整个会议期间常驻，不得按音频块重复加载

### 5.5 数据存储

- `Microsoft.Data.Sqlite`
- JSON、TXT、SRT 文件导出使用 .NET 内置 I/O 和 `System.Text.Json`
- SQLite 作为可靠的会话与字幕主索引，文件格式作为用户可读、可迁移的输出

### 5.6 密钥存储

- API Key 不得长期明文存储在 `appsettings.json` 或提交到版本控制。
- V1 优先使用 Windows DPAPI（当前用户范围）加密本地密钥；也可在用户确认后使用 Windows Credential Manager。
- 日志、异常消息和 UI 不得回显完整密钥。

## 6. 总体数据流

```text
Windows / Teams 系统输出声音
        │
        ▼
NAudio WASAPI Loopback
        │
        ├──► 音频格式转换（16 kHz / mono / int16）
        │         │
        │         ▼
        │    Channel<AudioChunk>（有界）
        │         │
        │         ▼
        │    Python Whisper Worker（常驻）
        │         │
        │         ▼
        │    partial / candidate final
        │         │
        │         ▼
        │    Transcript Stabilizer / Finalizer
        │         │
        │         ├──► WPF 字幕浮窗
        │         ├──► SQLite + JSON/TXT/SRT 实时保存
        │         └──► 日语 final 翻译队列
        │                         │
        │                         ▼
        │             公司 Azure OpenAI 兼容 API
        │                         │
        │                         ▼
        │                 中文字幕显示与保存
        │
        └──► PCM 命名管道 ─────────────┐
                                       │
屏幕或指定窗口 ─► FFmpeg 视频捕获 ─────┼──► meeting.mp4
                                       │
                     H.264 编码与 A/V 复用
```

背压策略必须明确：音频 Channel 使用有界容量，不允许无限增长。若识别速度短时跟不上，可合并相邻块或记录丢帧/延迟指标；不得悄悄耗尽内存。录音/录屏原始数据路径与识别路径应隔离，识别变慢不应主动破坏录制文件。

## 7. 工程结构

```text
KikuCaption/
├─ PROJECT.md
├─ README.md
├─ KikuCaption.sln
├─ Directory.Build.props
├─ .gitignore
├─ src/
│  ├─ KikuCaption.App/
│  │  ├─ Views/
│  │  ├─ ViewModels/
│  │  ├─ Converters/
│  │  ├─ Resources/
│  │  ├─ App.xaml
│  │  └─ appsettings.json
│  ├─ KikuCaption.Core/
│  │  ├─ Models/
│  │  ├─ Enums/
│  │  ├─ Interfaces/
│  │  └─ Events/
│  ├─ KikuCaption.Audio/
│  │  ├─ Capture/
│  │  ├─ Conversion/
│  │  └─ Buffering/
│  ├─ KikuCaption.Recording/
│  │  ├─ FFmpeg/
│  │  ├─ CaptureTargets/
│  │  └─ Muxing/
│  ├─ KikuCaption.Speech/
│  │  ├─ Worker/
│  │  ├─ Protocol/
│  │  ├─ Streaming/
│  │  └─ Stabilization/
│  ├─ KikuCaption.Translation/
│  │  ├─ Api/
│  │  ├─ Queue/
│  │  └─ Security/
│  ├─ KikuCaption.Storage/
│  │  ├─ Sqlite/
│  │  ├─ Export/
│  │  └─ Recovery/
│  └─ KikuCaption.Infrastructure/
│     ├─ Configuration/
│     ├─ Logging/
│     ├─ Processes/
│     └─ Diagnostics/
├─ python/
│  └─ whisper_worker/
│     ├─ main.py
│     ├─ recognizer.py
│     ├─ streaming.py
│     ├─ protocol.py
│     ├─ requirements.txt
│     └─ tests/
├─ tests/
│  ├─ KikuCaption.Core.Tests/
│  ├─ KikuCaption.Speech.Tests/
│  ├─ KikuCaption.Storage.Tests/
│  └─ KikuCaption.IntegrationTests/
├─ tools/
│  └─ ffmpeg/
└─ docs/
   ├─ Architecture.md
   ├─ Protocol.md
   └─ Verification.md
```

依赖方向：`Core` 不依赖 WPF、NAudio、FFmpeg、SQLite 或 HTTP 实现；其他功能项目依赖 `Core`，`App` 负责组合依赖。避免项目间循环引用。

## 8. 核心数据模型与接口设计

以下签名是架构意图，可在不改变职责和行为的前提下做小幅语言级调整。涉及数据流、隐私或功能范围的变化必须先获得用户确认。

### 8.1 数据模型

```csharp
public sealed record AudioChunk(
    ReadOnlyMemory<byte> Pcm,
    TimeSpan Timestamp,
    TimeSpan Duration,
    int SampleRate = 16000,
    int Channels = 1);

public enum TranscriptStatus
{
    Partial,
    Final,
    Translated,
    TranslationFailed
}

public sealed record TranscriptSegment
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }
    public required string Language { get; init; }
    public required string Text { get; init; }
    public string? Translation { get; init; }
    public required TranscriptStatus Status { get; init; }
    public double? Confidence { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record MeetingSession
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public required string RecognitionLanguage { get; init; }
    public required string OutputDirectory { get; init; }
    public string? RecordingPath { get; init; }
}
```

### 8.2 音频与录屏

```csharp
public interface IAudioCaptureService : IAsyncDisposable
{
    IAsyncEnumerable<AudioChunk> CaptureAsync(CancellationToken cancellationToken);
}

public interface IScreenRecorder : IAsyncDisposable
{
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken);
    RecorderState State { get; }
}
```

`RecordingOptions` 至少包含捕获类型（屏幕/窗口）、目标标识、输出路径、帧率、编码器偏好和是否包含系统声音。开始前必须探测 FFmpeg、编码器和目标窗口是否可用。

### 8.3 语音识别与 Worker

```csharp
public interface ISpeechRecognizer : IAsyncDisposable
{
    Task InitializeAsync(SpeechOptions options, CancellationToken cancellationToken);
    IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        CancellationToken cancellationToken);
}

public interface ITranscriptStabilizer
{
    StabilizationResult Process(TranscriptUpdate update);
    IReadOnlyList<TranscriptSegment> Flush(TimeSpan endTime);
}
```

C# 与 Python Worker 使用长期驻留进程。V1 优先采用 stdin/stdout JSON Lines 控制协议；音频体积或吞吐成为实际瓶颈时，可先提出命名管道升级方案并等待用户确认。`stdout` 只能输出协议消息，诊断信息写入 `stderr`。

协议至少包含：

- `initialize` / `ready`
- `audio`
- `partial`
- `final_candidate`
- `flush`
- `error`
- `shutdown`

每条消息包含协议版本、会话 ID 和关联序号。音频消息必须限制最大大小，并验证 Base64/PCM 长度。Worker 崩溃时主程序应捕获退出码和 `stderr`，保留已经落盘的数据，并允许有限次数重启。

### 8.4 存储与导出

```csharp
public interface ITranscriptRepository
{
    Task CreateSessionAsync(MeetingSession session, CancellationToken cancellationToken);
    Task UpsertSegmentAsync(TranscriptSegment segment, CancellationToken cancellationToken);
    Task CompleteSessionAsync(Guid sessionId, DateTimeOffset endedAt, CancellationToken cancellationToken);
}

public interface ITranscriptExporter
{
    Task ExportAsync(Guid sessionId, string outputDirectory, CancellationToken cancellationToken);
}
```

SQLite 至少包含 `MeetingSession`、`TranscriptSegment` 和 `TranslationJob`。final 字幕必须立即提交事务。JSON 可采用安全的临时文件加原子替换，SRT/TXT 可增量追加并在会后规范化重写。

### 8.5 翻译

```csharp
public interface IAiTranslationService
{
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public interface ITranslationQueue
{
    ValueTask EnqueueAsync(TranscriptSegment finalSegment, CancellationToken cancellationToken);
}
```

翻译必须在独立有界后台队列中执行，不阻塞识别、UI 或原文持久化。仅满足以下条件时入队：字幕状态为 final、源语言为 `ja`、翻译功能已启用、文本非空且未翻译。

建议翻译指令：

```text
你是日中会议实时翻译助手。将给定日语翻译成自然、简洁、准确的中文。
不要总结、解释或扩写；保留人名、产品名和 Azure、API、Sprint、Release 等技术词。
只输出翻译结果。
```

公司 API 可能不是微软官方 Azure OpenAI Endpoint，因此 Endpoint、认证头形式、API Key、模型名、API 版本和请求格式应可配置或封装在适配器中，不得硬编码官方端点假设。

## 9. 渐进字幕设计

Whisper 不是原生流式识别模型。V1 使用滑动音频窗口、重叠上下文、Stable Prefix 和 Finalizer 模拟渐进字幕。

- partial 刷新建议间隔：500～1000 ms。
- 推理音频窗口建议：2～6 秒，根据实测 RTF 调整。
- 相邻窗口 overlap：约 1～2 秒。
- 比较最近 2～3 次候选文本，提取重复稳定前缀。
- UI 中 final 使用正常亮色，partial 使用较淡颜色。
- partial 只存在内存/UI 中，是否写入调试追踪必须默认关闭。

Finalizer 综合判断：

1. VAD 检测到语音结束或连续静音约 500～800 ms；
2. 文本以合适标点结束；
3. 稳定前缀连续多次不变；
4. 达到最大句长或最大等待时间；
5. 用户停止时调用 `Flush`。

算法必须位于 `KikuCaption.Speech`，不得写入 ViewModel 或 code-behind。需要使用固定文本序列和预录音频进行可重复测试，重点避免 overlap 导致重复句子或漏词。

## 10. UI 范围

### 10.1 主窗口

- 开始/停止会话
- 选择整个屏幕或目标窗口
- 选择识别语言：日本語 / 中文；Auto 标记为实验功能且 V1 可不实现
- 开关日译中翻译
- 选择输出目录
- 展示录制时长、录屏/音频/识别/翻译状态
- 展示明确但不泄密的错误提示

### 10.2 字幕浮窗

- Always On Top
- 可拖动
- 可调字体大小和不透明度
- 可切换鼠标穿透
- 显示最近 2～5 行
- 原文与中文翻译双行显示
- partial 与 final 视觉区分
- 翻译延迟或失败不得遮盖原始字幕

## 11. 配置

示例 `appsettings.json`：

```json
{
  "Speech": {
    "Engine": "Whisper",
    "Model": "small",
    "ComputeType": "int8",
    "Language": "ja",
    "BeamSize": 1,
    "WindowSeconds": 4,
    "OverlapSeconds": 1.5
  },
  "Translation": {
    "Enabled": false,
    "Endpoint": "",
    "Model": "",
    "ApiVersion": "",
    "AuthenticationMode": "ApiKey",
    "TimeoutSeconds": 30,
    "MaxRetries": 3,
    "MaxQueueLength": 100
  },
  "Recording": {
    "FrameRate": 15,
    "PreferredEncoder": "h264_qsv",
    "FallbackEncoder": "libx264",
    "AudioSampleRate": 48000
  },
  "Subtitle": {
    "FontSize": 26,
    "Opacity": 0.85,
    "MaxLines": 4,
    "ClickThrough": false
  },
  "Storage": {
    "OutputDirectory": "Meetings",
    "MinimumFreeSpaceGb": 2,
    "LogRetentionDays": 14
  }
}
```

配置启动时必须验证。敏感值只在安全存储中保存，示例配置和版本库中只能出现空值或占位符。环境变量可用于开发环境，但不得打印其值。

## 12. 会话输出

```text
Meetings/
└─ 2026-08-08_130000_<session-id>/
   ├─ meeting.mp4
   ├─ transcript.json
   ├─ transcript.txt
   ├─ transcript.srt
   ├─ translation.srt
   └─ session.json
```

`transcript.json` 中每个 segment 至少包含：`id`、`sessionId`、`start`、`end`、`language`、`text`、`translation`、`status`、`confidence`、`createdAt`。

会议文件属于用户数据，不计入应用静态占用的 10 GB 目标，但程序必须在开始录制前检查可用磁盘空间，录制期间定期复查；达到阈值时安全停止录制、flush 字幕并明确通知用户。程序不得自行删除用户会议。

## 13. 安全、隐私与合规

- 用户应遵守公司政策和适用法律，在需要时取得参会者同意。
- 本工具不得设计为绕过 Teams 或公司的录制、转录通知与政策。
- 原始会议音频、视频和字幕默认只保存在用户选择的本地目录。
- 翻译只发送日语 final 文本，并在设置页清楚提示发送范围。
- partial、音频、视频、API Key 不得写入普通日志。
- HTTP 只允许 HTTPS；不得关闭 TLS 证书验证。
- 翻译请求超时、重试和错误响应不得泄露凭据。
- Python Worker 输入视为不可信边界：限制消息大小、验证字段和协议版本。
- FFmpeg 与 Python 子进程参数必须使用结构化参数列表，避免字符串拼接造成命令注入。
- 输出文件名和窗口标题不得未经清理直接成为路径。
- 依赖版本应固定或锁定，并记录来源和许可证。

## 14. 性能与磁盘预算

### 14.1 静态硬盘占用目标

以下为设计预算，最终以构建产物实测为准：

| 项目 | 目标预算 |
|---|---:|
| .NET 应用、自包含运行时及 NuGet 依赖 | ≤ 0.5 GB |
| Python 运行环境及包 | ≤ 2.5 GB |
| faster-whisper `small` 模型 | ≤ 1.5 GB |
| FFmpeg 与辅助工具 | ≤ 0.3 GB |
| 日志、缓存、升级余量 | ≤ 1.2 GB |
| 预留空间 | ≥ 4.0 GB |
| 合计上限 | < 10 GB |

不得同时捆绑多个大型 Whisper 模型。若允许切换 `base`，应按需下载并显示占用，或由用户明确选择替换 `small`，避免静默增加安装体积。模型下载必须支持完整性校验。

### 14.2 运行性能目标

- i5-1335、16 GB RAM、无独显环境下，`small/int8/beam_size=1` 的持续 RTF 应小于 1.0。
- 如实测无法稳定实时，应先报告数据，再建议用户切换 `base`、增大更新间隔或调整线程数；不得擅自降低功能或更换默认模型。
- UI 主线程保持响应；不得同步等待识别、FFmpeg 或翻译。
- CPU 不应因无限重试、忙轮询或无界队列长期满载。
- 录屏默认 15 FPS；Quick Sync 可用则优先使用，但必须有软件编码回退路径。
- 每个 Milestone 都应记录可复现的 CPU、内存、磁盘和延迟测量方法。

## 15. 日志与诊断

日志默认写入 `logs/app-yyyyMMdd.log`，按天滚动并按保留天数清理。记录：

- 应用版本、操作系统版本和非敏感配置摘要
- FFmpeg 与 Python Worker 启动/停止、退出码
- Whisper 模型加载时间
- 音频设备状态和格式变化
- 单次推理耗时、RTF、队列深度
- 翻译耗时、状态码类别、重试次数（不记录请求正文与密钥）
- 编码器选择、录制状态、音画同步诊断
- CPU、进程内存、剩余磁盘空间的周期性摘要
- 已脱敏异常及关联 ID

默认不得记录完整会议字幕、翻译文本、原始 PCM 或 HTTP Authorization Header。可另设显式、临时、带警告的诊断模式，但不属于 V1 必须功能。

## 16. 异常处理

必须至少覆盖以下情况：

1. 目标 Teams 窗口关闭或被最小化导致捕获失败。
2. 默认输出设备切换、设备断开或音频格式改变。
3. FFmpeg 不存在、版本不兼容、编码器不可用或异常退出。
4. Python 不存在、虚拟环境损坏、依赖缺失或模型不存在。
5. Whisper Worker 启动失败、协议损坏、超时或运行中崩溃。
6. 识别速度落后、Channel 满、内存持续升高。
7. 翻译 API 超时、401/403、429、5xx、网络断开或返回空结果。
8. SQLite 被锁、文件写入失败或输出目录失去访问权限。
9. 磁盘空间不足。
10. 用户主动停止、应用关闭或系统关机。

处理原则：

- 原始 final 字幕优先保存，翻译失败不能回滚原文。
- Worker 可采用有限次数、指数退避重启；不得无限重启。
- FFmpeg 异常退出后保留现有文件并报告是否可修复。
- 停止流程按顺序取消新输入、flush 音频/字幕、完成持久化、停止子进程并关闭会话。
- 任何降级行为都必须在 UI 和日志中明确显示。

## 17. Milestones 与验收标准

### Milestone 0：环境与工程骨架

**范围**

- 创建 .NET 10 WPF Solution 和本文件规定的基础项目结构。
- 配置依赖注入、配置、日志和测试项目。
- 建立 `Core` 模型/接口的最小集合。
- 增加环境检查器，报告 .NET、Python、FFmpeg 和可用磁盘空间；不下载大型模型。

**验收标准**

- Solution 在目标 Windows 11 环境可还原、编译和启动。
- 主窗口可打开且 UI 不阻塞。
- 所有初始测试通过。
- 缺少 Python/FFmpeg 时给出可理解提示，不崩溃。
- 提供运行、验证命令和实际结果。

### Milestone 1：WASAPI 系统音频捕获

**范围**

- 实现 `IAudioCaptureService` 和 WASAPI Loopback。
- 将输入转换为 16 kHz、mono、int16 PCM。
- 提供最小验证功能，把系统声音保存为 WAV。
- 正确开始、取消和停止捕获。

**验收标准**

- 播放 Teams 测试通话或本地音频时可得到可播放 WAV。
- WAV 格式和时长正确，无明显爆音或截断。
- 连续运行 30 分钟无无界内存增长。
- 切换/断开音频设备时可报告错误并安全停止或恢复。

### Milestone 2：本地 Whisper Worker 与通信

**范围**

- 创建 Python 常驻 Worker，加载 `small/int8` 一次。
- 实现 C# 子进程生命周期和 JSON Lines 协议。
- 支持 `ja`、`zh` 明确选择。
- 将 WAV/PCM 送入 Worker，得到带时间戳的识别结果。

**验收标准**

- 日语和中文测试音频均可输出可读文本。
- Worker 不为每个音频块重新启动或加载模型。
- 协议错误不会使 WPF 主进程崩溃。
- 在目标硬件记录模型加载时间、峰值内存、CPU 和 RTF。
- 进程关闭后无孤儿 Python 进程。

### Milestone 3：渐进字幕与 Overlay

**范围**

- 实现滑动窗口、overlap、Stable Prefix 和 Finalizer。
- 实现 `ITranscriptStabilizer` 单元测试。
- 创建 WPF 字幕浮窗，区分 partial/final。
- 支持置顶、拖动、字号/透明度与鼠标穿透开关。

**验收标准**

- 讲话时通常在 1～3 秒内出现 partial。
- 停顿后产生 final，且不频繁重复已经确认的文本。
- 停止会议时 pending 文本会合理 flush。
- UI 在持续识别 30 分钟时保持响应。
- 使用固定候选序列测试稳定前缀、重复消除和 final 条件。

### Milestone 4：字幕持久化与恢复

**范围**

- 实现 SQLite 会话、字幕和翻译任务表。
- 实时保存 final 字幕。
- 导出 JSON、TXT、原文 SRT。
- 实现异常退出后的会话发现与最小恢复/导出。

**验收标准**

- 每条 final 在产生后短时间内可从 SQLite 查询。
- JSON/TXT/SRT 时间顺序正确、UTF-8 内容正确。
- 强制终止测试后，已确认字幕仍存在且可导出。
- partial 不会污染最终 SRT。

### Milestone 5：FFmpeg V1 录屏与音画复用

**范围**

- V1 由 C# 直接启动 FFmpeg 捕获屏幕或指定窗口。
- 默认 15 FPS，探测 `h264_qsv`，失败时回退 `libx264`。
- 将 WASAPI 捕获的录制音频送入 FFmpeg 并输出 MP4。
- 实现录制状态、停止、异常退出和磁盘空间检查。

**验收标准**

- 能录制整个屏幕以及至少一种可靠的指定窗口方式。
- 输出 MP4 有画面和系统声音，可由常见播放器播放。
- 连续录制 30 分钟后，音画偏移目标不超过 500 ms；如不满足，必须量化并说明原因。
- Teams 窗口关闭、FFmpeg 崩溃或磁盘不足时安全停止并保留字幕。
- Quick Sync 不可用时自动使用软件编码且明确显示。

### Milestone 6：公司 Azure OpenAI 兼容 API 翻译

**范围**

- 实现 `IAiTranslationService` 和有界后台翻译队列。
- Endpoint、认证方式、模型和 API 版本可配置。
- 使用 DPAPI 或经确认的 Credential Manager 保存密钥。
- 仅翻译日语 final 字幕，保存中文译文并生成 `translation.srt`。
- 实现 timeout、取消、有限重试和 429/5xx 退避。

**验收标准**

- partial 不产生 API 请求。
- 日语 final 只提交一次，成功后 UI 和存储同步更新。
- 中文识别模式不触发日译中请求。
- 网络断开或认证失败时，原始字幕和录屏继续运行。
- 日志中不出现密钥、请求正文或完整字幕。

### Milestone 7：集成、稳定性与交付

**范围**

- 串联录屏、音频、识别、字幕、存储和可选翻译。
- 完善设置页、状态提示、取消和关闭流程。
- 完成资源监控、日志轮转、故障恢复和交付说明。
- 生成可重复的发布包与依赖清单。

**验收标准**

- 目标硬件上完成至少一次 60 分钟端到端测试。
- 应用静态硬盘占用实测小于 10 GB，并列出各组成部分。
- 首选运行内存小于 5 GB；若超过，须给出测量与原因，但不得超过 10 GB 硬限制。
- 识别 RTF 小于 1.0，常见字幕延迟 1～3 秒，UI 始终响应。
- 会话停止后 MP4、JSON、TXT、SRT 和可用的翻译 SRT 均有效。
- README、架构说明、安装/运行/验证/故障排除文档完整。

## 18. 每个 Milestone 的通用完成定义

Milestone 只有同时满足以下条件才算完成：

1. 只实现当前 Milestone 所需范围及其必要的最小支撑代码。
2. 代码能够编译，相关自动化测试通过。
3. 在可用环境中执行了实际验证；无法执行的验证必须明确标注，不能声称已通过。
4. 不以大量伪代码、空实现或始终成功的 mock 代替核心行为。
5. 更新相关文档，但未经用户确认不得修改本 `PROJECT.md` 的既定需求。
6. 报告修改文件、设计决定、已知限制、运行步骤和验证结果。
7. 完成后停止，不自动开始下一 Milestone。

## 19. 给 Claude Code 的执行提示词

下面的提示词应与本 `PROJECT.md` 一起提供给 Claude Code。首次执行时把 `{MILESTONE}` 替换为要实施的编号和名称，例如 `Milestone 0：环境与工程骨架`。

```text
你正在实现 KikuCaption Windows 桌面项目。仓库根目录中的 PROJECT.md 是已由用户确认的需求基线和最高优先级项目规范。开始前请完整阅读 PROJECT.md，并检查当前仓库状态、已有代码、测试和文档。

本次只允许实施：{MILESTONE}

严格规则：

1. 一次只做一个 Milestone。不得提前实现、顺手实现或部分开始后续 Milestone。
2. 只修改完成当前 Milestone 所必需的文件，保留用户已有改动，不覆盖无关内容。
3. 优先交付可编译、可运行、可验证的最小实现，不要用大量伪代码、空接口或假成功结果代替核心功能。
4. 使用 PROJECT.md 指定的 .NET 10 + WPF 架构、接口边界、安全/隐私规则、性能约束和磁盘预算。
5. 不得擅自修改 PROJECT.md 中的目标、非目标、技术选择、数据流、Milestone、验收标准或其他需求。
6. 如果发现架构、依赖、实现方式或 Milestone 安排存在更优方案、兼容性问题或明显风险：
   - 先停止相关实现；
   - 向用户说明现状、证据、推荐方案、收益、代价、迁移影响以及是否改变需求；
   - 明确请求用户确认；
   - 只有用户明确确认后，才允许修改 PROJECT.md 并按确认后的方案继续；
   - 用户未确认时，保持 PROJECT.md 不变，不自行采用新方案。
7. 普通的小型代码实现细节若不改变需求和模块职责，可以自行决定，并在完成报告中说明。
8. 所有密钥、会议音频、完整字幕和翻译文本均按 PROJECT.md 的隐私规则处理，不得写入版本库或普通日志。
9. 完成当前 Milestone 后必须停止，等待用户明确要求再开始下一个 Milestone。

执行顺序：

A. 阅读 PROJECT.md 和仓库现状。
B. 简短列出当前 Milestone 的实施计划、涉及模块和验证方法。
C. 实施当前 Milestone。
D. 编译并运行与当前范围相称的测试和实际验证。
E. 对照 PROJECT.md 中当前 Milestone 的每一条验收标准逐项检查。
F. 更新当前 Milestone 所需的 README、Architecture.md、Protocol.md 或 Verification.md；不要擅自修改 PROJECT.md。
G. 输出完成报告后停止。

完成报告必须包含：

- 本次实施的 Milestone
- 已完成内容
- 修改/新增的主要文件
- 关键设计决定
- 如何安装依赖
- 如何运行
- 如何由用户手工验证
- 自动化测试和实际执行过的命令及结果
- 对每条验收标准的通过/未通过/未验证状态
- CPU、内存、磁盘或延迟数据（若该 Milestone 要求）
- 已知限制、风险和未完成项
- 下一 Milestone 的名称，但不要开始实施

如果当前环境缺少完成实际验证所需的硬件、权限、Teams、音频设备、公司 API 或其他外部条件，请完成所有安全且可完成的工作，清楚标注哪些验收项“未验证”及原因，并给出用户可执行的精确验证步骤。不得把未执行的验证写成已通过。

现在请只实施 {MILESTONE}。完成后停止并等待用户确认。
```

### 推荐的首次指令

```text
请完整阅读仓库根目录的 PROJECT.md，并严格按照文档末尾的“给 Claude Code 的执行提示词”执行。

本次只实施 Milestone 0：环境与工程骨架。

完成后停止，报告如何运行、如何验证，并逐项说明该 Milestone 的验收结果。不要开始 Milestone 1。如果你发现更优方案或兼容性风险，先提出建议并请求我确认；未经确认不得修改 PROJECT.md 或擅自改变需求。
```
