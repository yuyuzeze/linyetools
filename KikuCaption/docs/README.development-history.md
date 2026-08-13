# KikuCaption

KikuCaption 是一个面向 Microsoft Teams 会议的个人 Windows 桌面工具：本机录屏 + 系统声音捕获、
本地 faster-whisper 日/中实时字幕、以及（可选）把日语 final 字幕发送到公司内部 Azure OpenAI 兼容
API 翻译为中文。完整需求见根目录 [`PROJECT.md`](PROJECT.md)（项目最高优先级规范）。

> 当前进度：**Milestone 7 — 集成、稳定性与交付**（已完成，PROJECT.md 规定的最后一个 Milestone）。在 M0–M6 + M3.1
> 之上新增：统一会话生命周期状态机（预检→创建→各子系统→安全停止，失败回滚、子系统故障隔离、幂等停止）、启动预检
> （通过/警告/阻断）、可复现资源采样（修复 CPU 采样）、日志轮转+启动清理、敏感信息自动扫描、用户设置持久化（不存密钥）、
> 关闭确认与安全停止、**自包含发布包 + 第三方许可清单 + SHA-256**。
> 交付/安装/卸载见 [`docs/UserGuide.md`](docs/UserGuide.md) 与 [`docs/Delivery.md`](docs/Delivery.md)；许可见
> [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。
>
> 保留状态：M5 录屏内容同步 ≤500ms、音频轨总时长可能比视频短约 1.9s（保留 Named Pipe，未切 WAV+remux）；M6 真实公司
> API 未验证（fake 端到端通过）。真实 60 分钟 Teams 端到端、真实公司 API、干净机/断网离线试用为**未验证**，步骤见
> [`docs/Verification.md`](docs/Verification.md)。

## 目录结构（当前已建立部分）

```text
KikuCaption/
├─ KikuCaption.sln
├─ Directory.Build.props / Directory.Packages.props / global.json
├─ src/
│  ├─ KikuCaption.Core/           # 领域模型、枚举、接口（不依赖 WPF/NAudio/FFmpeg/SQLite/HTTP）
│  ├─ KikuCaption.Infrastructure/ # 配置校验、Serilog 日志、环境检查器、进程调用
│  └─ KikuCaption.App/            # WPF 主程序（Generic Host + DI + MVVM）
├─ tests/
│  ├─ KikuCaption.Core.Tests/
│  └─ KikuCaption.Infrastructure.Tests/
├─ tools/ffmpeg/                  # 后续里程碑放置 ffmpeg.exe
└─ docs/                          # Architecture / Protocol / Verification
```

> `src/KikuCaption.Audio` 已在 Milestone 1 建立。`Recording|Speech|Translation|Storage` 与
> `python/whisper_worker` 将在其对应 Milestone 建立，避免现在用空项目填充（PROJECT.md 18.1、18.4）。

## 系统音频捕获（Milestone 1）

在主窗口的「系统音频捕获（Milestone 1 验证）」区：

1. 点击 **开始捕获…**，在弹出的保存对话框中选择 WAV 输出位置（默认建议为
   `<输出目录>/_audio_tests/system-audio_<时间戳>.wav`，不会覆盖已有文件）。
2. 播放要录制的系统声音（例如 Teams 测试通话或本地音乐）。
3. 点击 **停止** 结束并完成 WAV 文件。状态区显示已用时长、已写入音频大小和保存路径。

输出 WAV 固定为 **16000 Hz、单声道、16-bit PCM**。捕获、格式转换与文件写入均在后台完成，
不阻塞界面；音频设备断开或捕获失败会显示明确错误并安全停止，已写入的数据仍保留。

## 本地语音识别（Milestone 2）

### 建立 Python 环境（一次性）

Python **3.13.9** 已验证兼容（见 `docs/Verification.md`）。在仓库根目录：

```bash
python -m venv python/whisper_worker/.venv
python/whisper_worker/.venv/Scripts/python -m pip install -r python/whisper_worker/requirements.txt
# 运行 worker 单元测试（可选）：
python/whisper_worker/.venv/Scripts/python -m pip install -r python/whisper_worker/requirements-dev.txt
cd python/whisper_worker && ../.venv/Scripts/python -m pytest tests/
```

依赖版本锁定在 `python/whisper_worker/requirements.txt`（顶层）和 `requirements-lock.txt`（全量冻结）。

### 模型首次下载与位置

`small` 模型在**首次识别时自动下载**到模型缓存目录，默认 `<repo>/models/whisper`
（可用 `appsettings.json` 的 `Speech:ModelCacheDirectory` 或 worker 的 `--download-root` 配置）。
只使用 `small` 一个模型（约 486 MB）。**离线再次运行**：模型已缓存后无需联网，直接识别即可。

### 在应用内识别 WAV（选择 ja / zh）

主窗口「本地语音识别（Milestone 2 验证）」区：选择识别语言 **日本語(ja)** 或 **中文(zh)**，
点击 **识别 WAV 文件…** 选择一个 WAV，即可看到带时间戳的识别结果。首次会加载模型（约 1–2 秒，
未缓存模型时更久）。模型加载、音频编码与协议读取都在后台，不阻塞 UI。

### 实时音频（可选）

`ISpeechRecognizer.RecognizeAsync` 接受 `IAsyncEnumerable<AudioChunk>`，可直接接 Milestone 1 的
`IAudioCaptureService`（16k/mono/int16 实时 PCM）。M2 的最小验证入口以 WAV 为主；实时端到端串联在后续 Milestone。

### 确认没有孤儿进程

Worker 子进程被分配到 Windows Job Object（kill-on-close），并在关闭时优雅退出、超时才强杀。
关闭应用或结束识别后可检查：

```bash
tasklist | findstr /I python
```

正常情况下不应残留属于本应用的 python 进程。

### 测量 RTF / 内存 / 磁盘

- 真实模型的加载时间、RTF、进程内存与磁盘占用见 `docs/Verification.md`（含可复现命令）。
- 真实模型端到端 C# 集成测试（默认关闭）：

```bash
set KIKU_REALMODEL=1
dotnet test tests/KikuCaption.Speech.Tests/KikuCaption.Speech.Tests.csproj --filter Category=RealModel -v detailed
```

### 常见错误排查

| 现象 | 处理 |
|---|---|
| “找不到 Python 可执行文件/Worker 脚本” | 确认已建 venv，或在 `appsettings.json` 配置 `Speech:PythonExecutable`/`WorkerScript` |
| 首次识别很慢 | 正在下载 `small` 模型；完成后会缓存，二次很快 |
| “识别语言必须为 ja 或 zh” | 只支持明确选择 ja/zh，无 Auto |
| Worker 初始化超时 | 提高 `SpeechOptions.InitializeTimeout`，或检查依赖是否安装成功 |
| 识别中断但应用未崩溃 | Worker 错误被隔离；查看日志与 UI 提示，重试即可 |

## 实时字幕与字幕浮窗（Milestone 3）

### 启动 / 停止实时字幕

主窗口「实时字幕与字幕浮窗（Milestone 3）」区：

1. 选择语言 **ja / zh**。
2. 点击 **开始实时字幕**：程序加载模型（首次约 1–2 秒），开始捕获系统声音并显示渐进字幕。
3. 讲话/播放时先出现 **partial（淡色）**，停顿或句末稳定后转为 **final（亮色）**。
4. 点击 **停止** 结束；pending 文本会被 flush 为 final。

状态行显示当前状态与指标：`partial/final 数量、RTF、最近推理耗时、队列深度(ms)、背压跳过次数`。

### 字幕浮窗操作

- **显示/隐藏浮窗**：主窗口按钮切换。
- **置顶**：浮窗始终在最前（不抢占 Teams 焦点，使用 `WS_EX_NOACTIVATE`）。
- **拖动**：在浮窗上按住左键拖动（鼠标穿透关闭时）。
- **字号 / 透明度 / 最大行数**：主窗口滑块实时调整（最大行数 2–5）。
- **鼠标穿透**：勾选后点击穿透浮窗到下层窗口；**再次取消勾选可重新控制浮窗**。
- 主窗口关闭时浮窗一并关闭。

### 渐进识别参数（可配置，范围校验）

`ProgressiveCaptionOptions`（默认值遵循 PROJECT.md 9）：partial 间隔 500–1000 ms、窗口 2–6 s、
overlap 1–2 s、最近候选 2–3、静音 final 500–800 ms，另有最大句长/最大等待/稳定次数/最大行数。
`WindowSeconds`/`OverlapSeconds`/`MaxLines` 取自 `appsettings.json`（Speech/Subtitle），启动时校验，越界即报错。

### 性能测量方法

- 运行时指标见主窗口状态行；详细稳定性/内存/CPU/RTF 见 `docs/Verification.md`。
- 长时间稳定性（真实模型，播放连续音）：

```bash
set KIKU_REALMODEL=1
set KIKU_RT_MINUTES=15
dotnet test tests/KikuCaption.Speech.Tests --filter Category=Stability -v detailed
```

结果写入 `%TEMP%\kiku_rt_stability.txt`；停止后用 `tasklist | findstr python` 确认无孤儿。

## 字幕持久化与恢复（Milestone 4）

实时字幕运行时会自动持久化。详见 [docs/Storage.md](docs/Storage.md) 与 [docs/Recovery.md](docs/Recovery.md)。

- **数据库**：单个 SQLite 于 `<输出根>/kikucaption.db`（`PRAGMA user_version=1`，外键开启，WAL）。
- **会话目录**：`<输出根>/yyyy-MM-dd_HHmmss_<session-id>/`，含 `transcript.json/.txt/.srt` 与 `session.json`。
  输出根来自 `appsettings.json` 的 `Storage.OutputDirectory`（相对路径基于应用运行目录）。**不生成** `meeting.mp4`、`translation.srt`（录屏/翻译尚未实现）。
- **实时保存**：final 产生后立即写入 SQLite（UI 不等待磁盘）；JSON/TXT/SRT 采用去抖（默认 ~1 s）从 SQLite 重导出，
  停止时最终导出。最大文件延迟 ≈ 去抖间隔；SQLite 中 final 立即存在，恢复可从 SQLite 重建。
- **磁盘保护**：开始前检查 `Storage.MinimumFreeSpaceGb`，不足则拒绝并提示；运行中定期复查，跌破阈值时安全停止接收、
  flush、标记会话并提示。
- **崩溃恢复**：应用启动时自动扫描未完成会话，从 SQLite 重建文件并标记为 Recovered（幂等；损坏 JSON 会改名备份，
  数据库损坏则报错不谎称成功）。主窗口顶部显示恢复结果。
- **主窗口存储状态**：当前 Session、输出目录、已保存 final 数、最后保存时间、存储状态与错误。

手工验证（严格解析 SRT）：用支持 SRT 的播放器/字幕工具打开 `transcript.srt`，或按 `HH:mm:ss,fff` 格式校验时间轴。

## 录屏与音画复用（Milestone 5）

详见 [docs/Recording.md](docs/Recording.md) 与 [docs/FFmpeg.md](docs/FFmpeg.md)。

### FFmpeg 定位/安装
`Recording:FFmpegPath`（appsettings）→ 向上查找 `tools/ffmpeg/ffmpeg.exe`（+ffprobe）→ PATH。本项目已在
`tools/ffmpeg/` 放置 BtbN win64 **GPL** 构建（含 libx264 与 h264_qsv），二进制不提交 Git；部署时随附或配置路径。

### 录制流程
主窗口「实时字幕、字幕浮窗与录屏」区：选择**整个屏幕**或**指定窗口**（窗口可刷新列表），点击
**开始录制和字幕**。程序同时：加载模型跑实时字幕、启动 FFmpeg 录制到会话目录 `meeting.mp4`。点击**停止**
协调停止两条管线、优雅结束 FFmpeg 并 `ffprobe` 校验。默认 **15 FPS、H.264（QSV 可用则用，否则 libx264）、AAC 16 kHz**。

### 会话产物
```text
Meetings/<yyyy-MM-dd_HHmmss_session-id>/
  ├─ meeting.mp4        # M5 新增
  ├─ transcript.json / .txt / .srt
  └─ session.json       # 含 recordingPath
```

### 已知限制
gdigrab 无法可靠捕获最小化/硬件加速/DWM 窗口；录制音频为 16 kHz（V1 允许）；QSV 需 Intel 硬件（无则 libx264，CPU 更高）。
确认无孤儿：停止后 `tasklist | findstr /I "ffmpeg python"` 应为空。

## 日译中翻译（Milestone 6）

详见 [docs/Translation.md](docs/Translation.md) 与 [docs/Security.md](docs/Security.md)。

主窗口「日译中翻译（Milestone 6）」面板：

1. 填写 **Endpoint**（完整 HTTPS 地址）、**Model/Deployment**、可选 **API Version**、**认证模式**（`Bearer`/`ApiKeyHeader`/`None`）与 **Header 名**。
2. 在 **API Key** 的 `PasswordBox` 输入密钥并点「保存密钥」——以 **Windows DPAPI** 加密保存在 `%LOCALAPPDATA%/KikuCaption/secrets`，
   **不写入配置/日志/SQLite**，不要贴进代码或聊天。可「清除密钥」。
3. 勾选「启用日译中翻译」；用 **日本語(ja)** 识别。点「测试连接」（发送固定文本 `テスト接続`，不发真实字幕）。
4. 日语 final 出现后原文立即显示，翻译返回时**在同一卡片下方补上中文**（浮窗与右侧时间线均双行），并写入会话目录 `translation.srt`。

**只发送日语 final 文本**；不发送音频、partial、PCM、视频或整场历史。翻译失败保留原文、不影响录屏；应用重启自动恢复未完成的翻译任务。
配置项（`appsettings.json` 的 `Translation` 段）**绝不含 ApiKey**。

## 先决条件

| 依赖 | 版本 | Milestone 0 是否必需 |
|---|---|---|
| .NET SDK | **10.0** | 是（编译/运行/测试都需要） |
| Windows | 11 | 是（WPF 目标平台） |
| Python | 3.11（推荐） | 否（仅被检测；缺失只给提示） |
| FFmpeg | 近期版本 | 否（仅被检测；缺失只给提示） |

安装 .NET 10 SDK：<https://dotnet.microsoft.com/download/dotnet/10.0>

## 编译

```bash
dotnet restore KikuCaption.sln
dotnet build KikuCaption.sln -c Debug
```

## 运行

```bash
dotnet run --project src/KikuCaption.App/KikuCaption.App.csproj -c Debug
```

启动后主窗口会自动运行环境检查，列出 .NET 运行时、Python、FFmpeg 与可用磁盘空间的状态。
缺少 Python 或 FFmpeg 时会显示明确的中文提示，程序不会崩溃。点击“重新检查”可再次检测。

## 测试

```bash
dotnet test KikuCaption.sln
```

## 日志

运行时日志写入应用输出目录下的 `logs/app-yyyyMMdd.log`（按天滚动，默认保留 14 天）。
日志不记录密钥、完整字幕、翻译文本或原始音频（PROJECT.md 15）。

## 文档

- [docs/Architecture.md](docs/Architecture.md) — 模块与依赖方向
- [docs/Protocol.md](docs/Protocol.md) — C#↔Python Worker 协议（设计，M2 实现）
- [docs/Verification.md](docs/Verification.md) — 各里程碑验证步骤与结果
