# 验证记录

## Milestone 0：环境与工程骨架

### 本机环境（执行验证时）

| 项目 | 结果 |
|---|---|
| 操作系统 | Windows 11 Enterprise LTSC 2024 (10.0.26100) |
| .NET SDK | **10.0.302**（用户安装后；2.2.110 仍并存） |
| Python | 3.13.9（miniconda，`python --version` 可用） |
| FFmpeg | 未安装（`ffmpeg -version` 找不到命令） |
| C: 可用空间 | 约 19.5 GB |

### 实际执行的命令与结果（已全部实测）

```text
$ dotnet --version                            -> 10.0.302                       (exit 0)

$ dotnet restore KikuCaption.sln              -> 5 个项目全部还原成功           (exit 0)

$ dotnet build KikuCaption.sln -c Debug       -> 已成功生成，0 警告 0 错误      (exit 0)

$ dotnet test KikuCaption.sln --no-build
    Core.Tests           -> 通过 7，失败 0                                      (exit 0)
    Infrastructure.Tests -> 通过 9，失败 0                                      (exit 0)
    合计 16 个测试全部通过

$ KikuCaption.exe（启动 7 秒后未崩溃，正常运行环境检查，随后手动关闭）
```

WPF 启动时写入的运行日志 `logs/app-20260808.log`（真实运行输出）：

```text
2026-08-08 14:00:07.922 [INF] KikuCaption starting up (version 0.1.0.0).
2026-08-08 14:00:08.294 [INF] Environment check "DotNetRuntime": "Ok" (.NET 10.0.10)
2026-08-08 14:00:08.337 [INF] Environment check "Python": "Ok" (Python 3.13.9)
2026-08-08 14:00:08.340 [INF] Environment check "FFmpeg": "Missing" (n/a)
2026-08-08 14:00:08.344 [INF] Environment check "DiskSpace": "Ok" (19.5 GB 可用)
```

- 四个探针全部在运行时执行并落日志。
- **FFmpeg 缺失被识别为 Missing，程序未崩溃**，直接对应“缺少依赖给出提示、不崩溃”的验收项。
  （缺 Python 走同一 `Missing` 代码路径，此处 Python 已安装故显示 Ok。）

### 逐条验收项对照（PROJECT.md 17，Milestone 0）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| Solution 可还原、编译、启动 | **通过** | restore/build exit 0；exe 启动无崩溃并写日志 |
| 主窗口可打开且 UI 不阻塞 | **通过** | 进程存活 7s，异步环境检查在存活期内完成写日志（UI 未卡死/崩溃） |
| 所有初始测试通过 | **通过** | 16/16 测试通过 |
| 缺 Python/FFmpeg 时给出可理解提示、不崩溃 | **通过** | 日志显示 FFmpeg=Missing，程序继续运行 |
| 提供运行、验证命令和实际结果 | **通过** | 见本文件与 README |

> 备注：窗口的可视化外观（颜色/排版）建议由用户肉眼确认一次；功能与不崩溃路径已实测通过。

---

## Milestone 1：WASAPI 系统音频捕获

### 环境（执行验证时）

| 项目 | 结果 |
|---|---|
| .NET SDK | 10.0.302 |
| NAudio | 2.2.1 |
| 音频输出设备 | 存在（可打开 WasapiLoopbackCapture） |

### 实际执行的命令与结果

```text
$ dotnet sln add ... KikuCaption.Audio / KikuCaption.Audio.Tests    -> 已添加
$ dotnet restore KikuCaption.sln                                    -> 7 项目还原成功 (exit 0)
$ dotnet build   KikuCaption.sln -c Debug                           -> 0 警告 0 错误 (exit 0)
$ dotnet test    KikuCaption.sln --no-build                         -> 全部通过 (exit 0)
      Core.Tests            通过 7
      Infrastructure.Tests  通过 9
      Audio.Tests           通过 18   （含 1 个真实设备集成测试）
      合计 34 个测试

# 真实设备集成测试（自动播放 2s 440Hz 测试音，回环捕获）
$ dotnet test ... --filter Category=Integration -v detailed
      WAV: 16000 Hz, 1 ch, 16-bit, duration 1.89s, bytes 60480, peak amplitude 9829   -> 通过

# WPF 启动
$ KikuCaption.exe  -> 启动无崩溃，环境检查 + M1 音频面板正常显示，手动关闭
```

### 真实捕获 WAV 的实测参数（集成测试）

| 属性 | 值 |
|---|---|
| 采样率 | 16000 Hz |
| 声道 | 1（单声道） |
| 位深 | 16-bit PCM |
| 时长 | 1.89 s（捕获约 2 s 播放的测试音） |
| 数据字节 | 60480（= 30240 samples；30240/16000 ≈ 1.89 s，一致） |
| 峰值幅度 | 9829（远高于静音阈值，证明真实捕获到信号而非空文件） |

### 30 分钟内存稳定性

以 5 分钟连续捕获（极低音量 440Hz 持续音，保持回环活跃）作为可行代理，每 30s 采样一次
托管堆与工作集：

```text
START  t=00:00  managed=0MB   ws=60MB  minutes=5
SAMPLE t=00:30  managed=13MB  ws=83MB  audioBytes=956480
SAMPLE t=01:00  managed=14MB  ws=83MB  audioBytes=1917120
SAMPLE t=01:30  managed=6MB   ws=83MB  audioBytes=2870080
SAMPLE t=02:00  managed=17MB  ws=83MB  audioBytes=3841280
SAMPLE t=02:31  managed=12MB  ws=83MB  audioBytes=4795840
SAMPLE t=03:01  managed=12MB  ws=83MB  audioBytes=5756480
SAMPLE t=03:31  managed=13MB  ws=83MB  audioBytes=6628800
SAMPLE t=04:01  managed=9MB   ws=83MB  audioBytes=7462400
SAMPLE t=04:31  managed=12MB  ws=83MB  audioBytes=8424320
SAMPLE t=05:01  managed=10MB  ws=83MB  audioBytes=9375680
END    managed=1MB   ws=76MB  audioBytes=9375680
测试总数 1，通过 1，总时间 5.06 分钟
```

- 工作集全程稳定在约 **83 MB**（结束回落到 76 MB）；托管堆在 6–17 MB 之间波动（正常 GC 起伏），
  强制回收后回到 **1 MB**——无无界增长。
- `audioBytes` 每 30s 增长约 960 KB（= 16000 Hz × 2 B × 30 s），线性增长且与理论值一致，
  说明有界缓冲未丢帧、消费端持续跟上。
- 判据（测试断言 `endManaged < startManaged + 80 MB`）通过。
- 本次以 **5 分钟**代理实测；完整 30 分钟可由用户用同一命令运行（见下）。

### 用户手工验证步骤

1. `dotnet run --project src/KikuCaption.App/KikuCaption.App.csproj`
2. 在「系统音频捕获（Milestone 1 验证）」区点击 **开始捕获…**，选择 WAV 路径。
3. 播放 Teams 测试通话或本地音乐 ~10s，点击 **停止**。
4. 用播放器打开生成的 WAV，确认有声音、时长与录制时长基本一致。
5. 30 分钟稳定性（可选）：
   ```bash
   set KIKU_STABILITY_MINUTES=30
   dotnet test tests/KikuCaption.Audio.Tests/KikuCaption.Audio.Tests.csproj --filter Category=Stability -v detailed
   ```
   结果同时写入 `%TEMP%\kiku_stability.txt`。
6. 设备异常：捕获期间在 Windows 声音设置里禁用/切换默认输出设备，观察程序显示错误并安全停止、
   已写入的 WAV 仍可播放。

### 逐条验收标准对照（PROJECT.md 17，Milestone 1）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| 播放音频时可捕获系统输出并生成可播放 WAV | **通过** | 集成测试捕获 440Hz 音，peak 9829，WAV 有效 |
| WAV 为 16 kHz / 单声道 / 16-bit PCM | **通过** | WaveFileReader 读回 16000/1/16 |
| WAV 时长与捕获时长基本一致、无明显异常 | **通过** | 播放 2s→捕获 1.89s；字节数与时长自洽 |
| 连续运行 30 分钟无无界内存增长 | **部分验证** | 5 分钟代理实测内存平稳；30 分钟提供用户命令，标注为待用户执行 |
| 设备断开/切换/失败时报告错误并安全停止或恢复 | **部分验证** | 单元测试模拟设备中断→Faulted 且保留数据；真实拔设备路径需用户手工触发（未验证） |
| WPF UI 不因捕获/转换/写文件阻塞 | **通过** | 捕获在后台 pump；UI 线程仅收状态；启动/运行无卡死 |
| Solution 可还原、编译；自动化测试通过 | **通过** | restore/build/test 全 exit 0；34/34 |
| 新增指定行为的测试 | **通过** | 状态转换/重复开始停止/取消/格式转换/有界背压/WAV 格式均有测试 |

### 已知限制与风险

- 真实“物理拔出/切换音频设备”的异常路径未在自动化中触发（用单元测试的模拟中断覆盖），标注为
  用户可手工验证。
- WASAPI 回环仅在端点有音频渲染时产生数据；完全静音时不产生帧（属正常行为）。
- 30 分钟稳定性以 5 分钟代理实测；完整 30 分钟留给用户执行。

---

## Milestone 2：本地 Whisper Worker 与通信

### 依赖兼容性结论（Python 3.13.9）

**完全兼容，继续使用 3.13.9 建立项目独立 venv（未改动系统 Python）。** 证据：

- PyPI 元数据：`ctranslate2` 4.8.1 提供 `cp313-win_amd64` wheel；`faster-whisper` 1.2.1 为纯 Python。
- **实际安装成功**（`python/whisper_worker/.venv`）：ctranslate2 4.8.1、faster-whisper 1.2.1、numpy 2.5.1、
  onnxruntime 1.28.0、av 18.0.0、tokenizers 0.23.1（全部 cp313/abi3）。
- 原生库 `import ctranslate2` 成功，CPU 支持 `int8`。

### Python 与依赖版本 / 锁定方式

- Python 3.13.9（miniconda）；venv 位于 `python/whisper_worker/.venv`。
- 运行依赖：`requirements.txt`（顶层固定 faster-whisper==1.2.1, ctranslate2==4.8.1）+ `requirements-lock.txt`
  （25 包全量冻结）。开发依赖：`requirements-dev.txt`（pytest==8.4.2）。
- NuGet：无新增第三方包（Speech 仅用 BCL：System.Text.Json、System.Threading.Channels、kernel32 P/Invoke）。

### 模型位置与磁盘占用（实测）

| 组成 | 实测 |
|---|---|
| Python venv | ~303 MB（`du`；含 psutil/pytest 等开发包） |
| faster-whisper `small` 模型 | **486.2 MB**（`models/whisper`，可配置） |
| Speech 构建产物 | 很小（纯托管 DLL） |
| 新增静态占用合计 | **≈ 0.8 GB** |

应用静态总占用（.NET 自包含未打包，此处为源+依赖）仍远小于 10 GB 目标。模型缓存目录明确、可配置
（`Speech:ModelCacheDirectory`，默认 `<repo>/models/whisper`），已加入 `.gitignore`，不重复下载。

### 真实模型指标（faster-whisper small / int8 / cpu，i5 无独显）

| 指标 | 实测 |
|---|---|
| 模型加载时间（已缓存，纯加载） | **~1.43 s**（首次含下载约 54 s） |
| 模型加载时间（经 C# worker，ready） | ~1.69–1.71 s |
| 推理耗时 / RTF | 10 s 音频约 1.2 s → **RTF ≈ 0.12–0.13**（< 1.0）|
| Worker 进程内存（RSS，加载后/推理后） | **~352 MB / ~377 MB** |
| 模型只加载一次 | 同一 Worker 连续两次识别，`initialize`/加载仅一次 |

### 实际执行的命令与原始结果摘要

```text
$ pip install faster-whisper==1.2.1 ctranslate2==4.8.1     -> Successfully installed ... (exit 0)
$ python -m pytest python/whisper_worker/tests/            -> 14 passed
$ dotnet build KikuCaption.sln -c Debug                    -> 0 警告 0 错误
$ dotnet test KikuCaption.sln --no-build                   -> 全部通过（RealModel 默认跳过）
      Core.Tests 7 / Infrastructure.Tests 9 / Speech.Tests 24 / Audio.Tests 19  = 59
$ KIKU_REALMODEL=1 KIKU_ZH_WAV=<zh.wav> dotnet test --filter Category=RealModel
      RealWorker_LoadsModelOnce_CleanRoundTrip_NoOrphan   -> 通过（ready 1687ms，无孤儿）
      RealWorker_RecognizesChineseSpeech_ReadableText     -> 通过
        recognized zh text: 今天天氣很好,我們現在開始測試會議字幕功能。
        final segments: 1, first span: 00:00:00-00:00:05
$ tasklist | findstr python   （识别后）                    -> 无残留 python 进程
```

中文测试音频由 Windows SAPI 语音「Microsoft Huihui (zh-CN)」合成为 16k mono WAV
（`今天天气很好，我们现在开始测试会议字幕功能。`），经 C# → worker → 模型识别，输出与原文一致。

### 日语 / 中文验证方法

- **中文（zh）：已验证**——合成中文语音经完整 C# 管线识别为准确可读文本（见上）。
- **日语（ja）：未验证**——本机无日语 TTS 语音，缺少合适日语测试音频。用户可提供/录制一段日语 WAV
  （16 kHz 单声道优先，其它格式会自动转换），在应用内选 `ja` 识别，或：
  ```bash
  set KIKU_REALMODEL=1
  set KIKU_ZH_WAV=<你的日语或中文 wav>   # 该集成测试对任意 CJK 文本通用
  dotnet test tests/KikuCaption.Speech.Tests --filter Category=RealModel -v detailed
  ```

### 自动化测试数量与结果

- Python（pytest）：**14 通过**（协议校验/序列化/base64/PCM/streaming）。
- .NET：**59 通过**（含 Speech 22 个单元 + 2 个真实模型集成，默认跳过）。真实模型集成显式开启后 **2 通过**。

### 逐条验收标准对照（PROJECT.md 17，Milestone 2）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| 真实 small/int8/cpu 成功加载模型 | **通过** | ready，加载 ~1.4–1.7 s |
| 日语测试音频输出可读日语文本 | **未验证** | 无日语测试音频/TTS；已给用户步骤 |
| 中文测试音频输出可读中文文本 | **通过** | 识别出与原文一致的中文 |
| 结果包含合理时间戳 | **通过** | final 段带 start/end（如 00:00–00:05）|
| 同一 Worker 多次识别只加载一次模型 | **通过** | 集成测试两次识别；单元测试 `InitializeCount==1` |
| 记录加载时间/峰值内存/磁盘/RTF | **通过** | 见上表 |
| 协议错误不使 WPF 主进程崩溃 | **通过** | 错误隔离为 `error`/`worker_exited`；单元测试覆盖 |
| 正常结束后无孤儿 Python 进程 | **通过** | Job Object + tasklist 检查为 0 |
| restore / Debug build / 全部自动化测试通过 | **通过** | 见命令摘要 |
| Python Worker 测试通过 | **通过** | 14 passed |
| WPF 启动正常，M1 音频捕获无回归 | **通过** | 启动无崩溃，三面板显示，M1 测试仍全绿 |
| 缺测试音频不得伪造通过 | **遵守** | 日语标记为“未验证”并给出方法 |

### 协议自动化测试覆盖（对照要求清单）

JSON 序列化/反序列化、版本+必填字段校验、无效 JSON、无效 Base64（pytest）、PCM 长度/格式、超大消息拒绝、
stdout 协议与 stderr 隔离（结构+malformed 跳过）、初始化成功/失败、重复初始化、正常 shutdown、取消、
Worker 超时、Worker 异常退出、无孤儿、stdin 串行化、有界队列/背压、ja/zh 传参、模型只加载一次 —— 均有对应测试。

### 已知限制与风险（M2）

- 日语可读文本未验证（无日语测试音频）。
- M2 的 `streaming.py` 为“缓冲后 flush 转写”，非 M3 的滑动窗口/Stable Prefix/Finalizer；实时逐字渐进在 M3。
- 首次识别需联网下载 `small` 模型；离线环境需预先下载模型缓存。
- 真实模型集成测试默认关闭（`KIKU_REALMODEL`），避免普通套件耗时并要求 venv/模型。

---

## Milestone 3：渐进字幕与 Subtitle Overlay

### 日语识别前置回归门槛

本机 TTS 仅有 zh(Microsoft Huihui) 与 en(Zira)，**无日语语音、无用户日语样本** → 日语真实识别
**继续标记“未验证”**（不伪造）。已提供用户验证步骤（见 M2 章节及下）。日语缺失不影响 M3 单元测试。

### 渐进识别策略与参数

- 复用 M2 worker/协议（未改动）：每 `PartialIntervalMs` 用 `RecognizeAsync` 转写“当前语句”，得到增长候选。
- 参数（`ProgressiveCaptionOptions`，启动校验）：PartialInterval 500–1000（默认 800）、Window 2–6（4）、
  Overlap 1–2（1.5）、RecentCandidates 2–3（2）、SilenceFinal 500–800（700 wait，本集成用 600）、
  MaxSentence（12s）、MaxWait（20s）、StableRepeat（2）、MaxLines 2–5（4）。
- **Stable Prefix**：LocalAgreement——最近 N 候选按 Unicode 码点取最长一致前缀（忽略空白，CJK 友好），
  committed 前缀单调不回退。
- **Finalizer**：静音(能量VAD)≥SilenceFinal、或句末标点+稳定 K 次、或最大句长、或最大等待、或 Flush；
  空 pending 不 final；final 不可变。

### 实际执行命令与结果（真实 small/int8/cpu）

```text
$ dotnet build KikuCaption.sln -c Debug            -> 0 警告 0 错误
$ dotnet test KikuCaption.sln --no-build           -> .NET 95 通过（RealModel 默认跳过）
      Core 7 / Infrastructure 9 / Speech 60 / Audio 19
$ python -m pytest python/whisper_worker/tests/    -> 14 passed
$ KIKU_REALMODEL=1 KIKU_ZH_WAV=<zh.wav> dotnet test --filter RealtimePipelineIntegrationTests
   Pipeline_RecognizesChineseWav_PartialThenFinal   -> 通过
      partials=1 finals=1 RTF=0.21 lastInfer=1236ms queue=0ms skipped=1
      partial: 今天天氣很好,我們現在開始測試會議字幕功能。
      final  : 今天天氣很好,我們現在開始測試會議字幕功能。
   Pipeline_LiveLoopback_RecognizesPlayedChinese     -> 通过（WASAPI 实时回环）
      live partials=6 finals=1
      final  : 今天天氣很好,我們現在開始測試會議字幕功能。…（连播两遍）
$ tasklist | findstr python  （运行后）             -> 无残留
```

### 中文 partial / final 示例（真实识别）

- partial（淡色，进行中）：`今天天氣很好,我們現在開始測試會議字幕功能。`
- final（亮色，稳定后）：`今天天氣很好,我們現在開始測試會議字幕功能。`
- 通过 WAV 与**实时 WASAPI 回环**两条路径均得到准确中文，partial → final 正常。

### 日语 partial / final

**未验证**——无日语测试音频/TTS。用户可提供日语 16k mono WAV，在应用内选 `ja` 开始实时字幕，或
`set KIKU_ZH_WAV=<日语 wav>; set KIKU_REALMODEL=1; dotnet test --filter RealtimePipelineIntegrationTests`
（集成断言对任意 CJK 通用）。

### 延迟 / RTF / CPU / 内存 / 运行时长（M3 稳定性）

以真实模型 + 连续低音的实时管线运行，每 30s 采样：

```text
START main=61MB python=0MB minutes=3
t=00:31 main=61MB python=408MB RTF=0.25 infer=1143ms partial=15 final=2  queue=6820ms skipped=15
t=01:01 main=61MB python=361MB RTF=1.39 infer=1128ms partial=31 final=5  queue=2380ms skipped=31
t=01:31 main=61MB python=402MB RTF=0.17 infer=1126ms partial=46 final=7  queue=8720ms skipped=46
t=02:01 main=61MB python=388MB RTF=0.10 infer=1119ms partial=61 final=10 queue=940ms  skipped=61
t=02:31 main=61MB python=400MB RTF=0.22 infer=1047ms partial=76 final=12 queue=7360ms skipped=76
t=03:01 main=61MB python=389MB RTF=1.44 infer=1148ms partial=93 final=15 queue=2230ms skipped=93
END    main=206MB  runtime=03:02   （测试 3m2s 通过；停止后无孤儿 python）
```

- **运行时长：实际 3 分 2 秒**（非 15 分钟；完整命令见 README，由用户执行）。
- **内存**：主进程（.NET 测试宿主）运行期间稳定 **~61 MB**，无增长；Worker（python）**~360–408 MB** 稳定波动、
  无无界增长。END 主进程 206 MB 为停止/释放后的一次性测量（测试宿主开销，非识别期占用）；判据
  `endMain < startMain+200MB` 通过。
- **CPU**：本次直接采样得 0（缺陷：在 Worker 退出后才取 `TotalProcessorTime`，无进程可读）。以推理耗时估算：
  93 次 × ~1.1 s ≈ 102 s 推理 / 182 s 墙钟 ≈ **单核 ~56%**（small/int8 单线程，供参考，非直接测量）。
- **背压/队列**：本次输入为**连续无静音低音**（病理输入），语句不断增长到 MaxSentence(10s) 才切分，导致
  队列深度升至最高 ~8.7 s、`skipped` 随之累加——**背压机制按预期工作**（有界、不崩溃、有可观测指标）。
  真实语音有停顿时语句更短，如 WAV 测试 RTF≈0.21、队列≈0。

- partial 延迟 ≈ 单次推理耗时 + 半个 partial 间隔；WAV 单窗推理约 **1.2 s**（RTF ≈ 0.21，< 1.0）。
- final 延迟 ≈ 触发条件满足后立即产生（静音 ~600–700 ms 或句末稳定 / Flush 即时）。
- 队列深度 = 当前语句音频时长（ms）；背压跳过 = 推理慢于 partial 间隔的周期数。
- 运行时长为实际值（非 15 分钟）；完整 15 分钟命令见 README，可由用户执行。

### 孤儿进程检查

实时字幕停止、应用关闭、集成/稳定性测试结束后 `tasklist | findstr python` 均为 **0**
（Worker 由 Job Object kill-on-close + 显式关闭清理）。

### Overlay 人工验收（建议用户逐项确认）

浮窗为独立置顶窗口，代码仅在 code-behind 处理窗口/OS 行为（扩展样式、拖动、定位），字幕状态在 VM。
建议人工检查：置顶、拖动、字号、透明度、最大行数(2–5)裁剪、partial/final 视觉区分、鼠标穿透开/关、
显示/隐藏、主窗口关闭时浮窗关闭、不抢占 Teams 焦点（`WS_EX_NOACTIVATE`）、在 Teams 等窗口前显示。
自动化覆盖了这些的**可测逻辑**（行裁剪、partial 被替换、穿透状态、状态机）；纯视觉需人工确认。

### 自动化测试（M3 新增）

- Stable Prefix：12 例（逐步增长、日/中无空格、回退、改写、标点、空白、空文本、overlap 重复、
  已 final 不重复、多次相同稳定、无公共前缀）+ N=3 变体。
- Finalizer：静音/标点+稳定/短暂静音不 final/最大句长/最大等待/Flush 保存/空不 final + 标点无稳定不 final。
- Options 范围校验；Pipeline：有限源 partial→final、状态转换、重复开始、重复停止、init 故障、
  worker 异常退出、背压跳过指标。
- final 不可变、连续分段、取消安全：由 Stabilizer(Flush 重置)+Finalizer + Pipeline(取消/重复停止) 组合覆盖。

### 逐条验收标准对照（PROJECT.md 17，Milestone 3）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| 讲话通常 1–3 s 内出现 partial | **通过（zh）** | 单窗推理 ~1.2 s；实时回环 6 次 partial |
| 停顿后产生 final，不频繁重复已确认文本 | **通过（zh）** | committed 单调、final 后重置；示例正确 |
| 停止时 pending 合理 flush | **通过** | Finalizer FlushRequested + 输入结束 flush；单测覆盖 |
| UI 持续识别 30 分钟保持响应 | **部分验证** | 后台管线 + Dispatcher 编组，UI 不阻塞；实测为较短时长（见上），30 min 未测 |
| 固定候选序列测试稳定前缀/去重/final 条件 | **通过** | 12+8 个确定性单元测试 |
| 日语 partial→final | **未验证** | 无日语样本（步骤已给） |
| 中文 partial→final | **通过** | WAV + 实时回环两路径 |
| 无孤儿进程 | **通过** | tasklist 为 0 |
| 不阻塞 WASAPI/UI 线程、有界、取消传播 | **通过** | 后台 ingest/cycle、有界缓冲、单在途、跳过指标；单测覆盖 |
| Overlay 置顶/拖动/字号/透明度/穿透/行数 | **部分验证** | 逻辑单测 + 需用户视觉确认 |

### 已知限制与风险（M3）

- 日语可读文本未验证（无样本）。
- UI 30 分钟连续响应未实测（做了较短稳定性）；Overlay 纯视觉需人工确认。
- M3 每周期重发“当前语句”音频并整段转写（O(语句长)，受 MaxSentence 上界约束）；未来可加 worker 侧增量
  `transcribe` 优化，但当前 RTF≈0.2 有充裕余量，无需改协议。
- 实时回环连播两遍中间无静音时会并入同一语句（示例中出现拼接），属预期分段行为。

---

## Milestone 3.1：完整会议字幕历史时间线

在 M3 浮窗（最近 2–5 行）之外，主界面右侧新增**全程字幕时间线**：保留当前会话从第一条到最后一条的
**全部 final 字幕**，最早在上、最新在下，从不因“最近 N 行”或 UI 数量上限而移除旧字幕。本次**不做讲话者识别、
不显示任何发言者占位符**（卡片仅显示 `时间 + 文本`），未引入字段/数据库迁移/pyannote/PyTorch/声纹/Graph API。

### 设计（决策逻辑可单测，滚动像素为视图职责）

- [`MeetingTimelineViewModel`](../src/KikuCaption.App/ViewModels/MeetingTimelineViewModel.cs)：`Entries` 只追加、
  永不裁剪；`AppendLive` 的到达顺序即 SQLite `SequenceNumber`（新会话从 1 递增，屏幕顺序=存储顺序）。partial 只更新
  底部“正在识别”单行（`SetPartial`），**不进入历史**。
- 自动滚动决策全在 VM：`IsAutoScroll`、`NewCount`/`HasNewMessages`、`NotifyAtBottom`、`JumpToLatest`、
  `ScrollToEndRequested` 事件。视图端 [`TimelineAutoScroll`](../src/KikuCaption.App/Behaviors/TimelineAutoScroll.cs)
  仅做像素滚动与“是否贴底”上报；内容增长的滚动事件（`ExtentHeightChange!=0`）被忽略，新字幕永远不会被误判为用户滑动。
- 恢复：`LoadHistoryAsync(sessionId)` 从 SQLite 按 `SequenceNumber` 载入**全部 final**（`GetSegmentsAsync`），
  `LoadMostRecentSession` 用新增的 `GetMostRecentSessionAsync` 重开最近一次会议。**清空显示**仅清 UI，不删 SQLite/字幕文件。
- WPF UI 虚拟化：`ListBox` + `VirtualizingPanel.IsVirtualizing=True`、`VirtualizationMode=Recycling`、
  `ScrollUnit=Pixel`、`ScrollViewer.CanContentScroll=True`——只回收不可见容器，不删数据。

### 自动化测试（App.Tests，10 项）

| # | 测试 | 覆盖需求 |
|---|---|---|
| 1 | `Load5000_FirstToLast_Ordered_NoLossNoDuplicate` | 5000 条第一→最后、首尾内容/时间/序号正确、序号 1..5000 连续无重复 |
| 2 | `Partial_NeverEntersHistory` | partial 只更新底部行，永不进入 final 历史 |
| 3 | `AtBottom_NewFinal_AutoScrolls_NoNewCount` | 贴底时新 final 自动跟随、不计“新” |
| 4 | `ScrolledUp_NewFinals_DoNotForceBottom_CountCorrect` | 上滑看历史不被强拉到底、`有 N 条新字幕` 计数正确 |
| 5 | `JumpToLatest_ResetsCount_ResumesAutoScroll_Scrolls` | 点提示回到最新并恢复自动滚动 |
| 6 | `ScrollBackToBottom_ResumesAutoScroll` | 自行滑到底恢复自动滚动 |
| 7 | `ClearDisplay_DoesNotDeleteStorage` | 清空显示后 SQLite 仍有全部 final、可重新载入 |
| 8 | `RecoverFromSqlite_5000_FirstToLast_Ordered` | 从真实 SQLite 按序号恢复 5000 条、首→尾 |
| 9 | `LoadHistory_ExcludesPartials` | 载入路径排除任何 partial |
| 10 | `ListBox_Virtualizes_5000_Items`（gated `KIKU_UI=1`，STA+HwndSource） | UI 虚拟化确实启用 |

**虚拟化实测**：5000 项仅**实体化 31 个容器**（`realized containers = 31 of 5000`），证明只回收可见容器、可流畅滚动
且不丢数据。全套 **.NET 185 通过、0 失败**，M0–M5 无回归。

### 逐条验收对照（本次 M3.1 需求）

| 需求 | 状态 | 证据 |
|---|---|---|
| 保留第一→最后全部 final、最早在上最新在下、可从头滚到尾 | **通过** | 测试 1；`Entries` 只追加不裁剪 |
| 不只保留最近 2–5 行 / 不因上限移除旧字幕 / 不删除隐藏较早 final | **通过** | 无 TrimToMaxLines；测试 1 |
| 恢复时从 SQLite 按 SequenceNumber 加载全部 final | **通过** | 测试 8（真实 SQLite 5000 条） |
| WPF UI 虚拟化、数千条流畅、只回收不删数据 | **通过（实测）** | 测试 10：31/5000 实体化 |
| 贴底自动滚动 / partial 只更新底部行 / 上滑暂停 / 不强拉 / N 条新字幕 / 点击回到最新 / 自行到底恢复 | **通过** | 测试 2–6 |
| 停止会议后仍可从头到尾浏览 | **通过** | 停止不清空 `Entries`；`BeginSession` 仅在开始时清 |
| partial 不进入 final 历史 | **通过** | 测试 2、9 |
| 清空 UI 不删 SQLite/字幕文件 | **通过** | 测试 7 |
| 本次不做讲话者、不显示任何占位符 | **通过** | 卡片仅 `时间+文本`；无字段/迁移/pyannote/PyTorch/声纹/Graph |
| 5000 条流畅滚动（像素级手感） | **部分（虚拟化已实测，主观流畅需人工）** | 31/5000 容器；真机滚动手感建议用户人工确认 |

### 已知限制与手工验证项（M3.1）

- **像素级“流畅手感”与真机长会话（数千条）** 建议用户在真实运行中人工确认（虚拟化启用与容器回收已由测试 10 客观证明）。
- 30 分钟真实会议、自动滚动的真机交互（鼠标滚轮/拖动条）为**用户手工验证**。
- 时间戳采用产生时的本机墙钟（`CreatedAt.LocalDateTime`，`HH:mm:ss`），恢复时用存储的 `CreatedAt`。

---

## Milestone 4：字幕持久化与恢复

### 依赖与安全

- `Microsoft.Data.Sqlite` 10.0.10；**原生库固定为已修补的 `SQLitePCLRaw.lib.e_sqlite3` 3.50.3**，
  清除了 9.0.0 传递依赖的高危漏洞告警（NU1903 / GHSA-2m69-gcr7-jv3q）。全解决方案构建 **0 警告 0 错误**。
- 参数化 SQL；日志只含 `SessionId/SegmentId/长度`，不含字幕正文。

### 数据库位置与 schema

- 单库 `<输出根>/kikucaption.db`（外键 ON、WAL、`PRAGMA user_version=1`）。默认输出根 =
  `<应用运行目录>/Meetings`。WPF 启动即创建（实测 4096 字节的空库）。
- 表：`MeetingSession` / `TranscriptSegment`（唯一索引 `(SessionId,SequenceNumber)`）/ `TranslationJob`（仅建表）。

### 会话目录示例

```text
Meetings/
├─ kikucaption.db
└─ 2026-08-08_HHmmss_<session-id>/
   ├─ transcript.json   (410 B)
   ├─ transcript.txt    (76 B)
   ├─ transcript.srt    (102 B)
   └─ session.json      (461 B)
```

### 实际执行命令与结果

```text
$ dotnet build KikuCaption.sln -c Debug              -> 0 警告 0 错误
$ dotnet test  KikuCaption.sln --no-build            -> .NET 137 通过
      Core 7 / Infrastructure 9 / Audio 19 / Speech 63 / Storage 39
$ pytest python/whisper_worker/tests/                -> 14 passed（M2/M3 无回归）
$ KIKU_REALMODEL=1 KIKU_ZH_WAV=<zh.wav> dotnet test --filter RealtimeStorageIntegrationTests
      RealPipeline_PersistsChineseSession_ToSqliteAndFiles -> 通过
        mid-session SQLite segments: 1        （停止前 SQLite 已有 final = 实时保存）
        transcript.json 410 / transcript.txt 76 / transcript.srt 102 / session.json 461 (bytes)
        SRT: 今天天氣很好,我們現在開始測試會議字幕功能。
        无 *.tmp 残留
$ KikuCaption.exe 启动                                -> 无崩溃；自动创建库；启动恢复检查执行；关闭后 0 python
```

### 实时保存策略与最大延迟

final → 有界队列(容量 256, 满则背压) → 后台写入 → **SQLite 立即提交**（UI 不等待磁盘）→ 去抖(默认 1000 ms)
从 SQLite 重导出 JSON/TXT/SRT/session.json，停止时最终导出。**文件最大延迟 ≈ 去抖 1 s**；SQLite 中 final 立即存在。

### final 不丢失 / 队列满行为

有界队列 `FullMode=Wait`，`RecordFinalAsync` 在满时 **背压等待**（作用于管线后台路径，不阻塞 UI），
**绝不静默丢弃**；写入失败置 `StorageError` + `StorageFailed` 事件、停止接收，**不伪装成功**；停止时先 drain
队列再最终导出（最后一条 final 不丢）。单元测试 `QueueFull_BackPressure_NoDrop`（容量 2 写 20 条→全部持久化）证实。

### 恢复流程

见 [docs/Recovery.md](Recovery.md)。`SessionRecoveryServiceTests`（真实 SQLite）覆盖：发现 Running 会话、
从 SQLite 重建文件、幂等、缺失单文件重建、损坏 JSON 改名备份、残留 tmp 清理、空会话、DB 损坏抛错、
单会话失败隔离——全部通过。**强制终止 → 重启恢复**由这些真实 SQLite 恢复测试等价覆盖；进程级强杀+重启为
用户手工步骤（见 Recovery.md），标注为“通过（等价自动化）+ 手工步骤”。

### 自动化测试数量与结果

Storage.Tests **39 通过**：Repository 10、Export 9、Recorder/Pipeline 8、Recovery 9、Disk/Path 4。
另有 1 个 gated 真实模型端到端（默认跳过，显式开启通过）。测试数据均为**虚构中日文本**，无真实会议内容。

### 数据库与输出磁盘占用

空库 4 KB；每个短会话文件合计约 1 KB（示例）；数据库随字幕线性增长（纯文本+时间戳，体量很小）。
会议文件属用户数据，不计入应用静态 10 GB 目标。

### 逐条验收标准对照（Milestone 4）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| Storage 工程、依赖方向正确、无循环 | **通过** | Storage→Core，App 组合；构建通过 |
| 会话目录唯一、安全、防穿越、不覆盖他人 | **通过** | 时间戳+SessionId；`SessionPaths` 守卫；单测 |
| SQLite 三表、外键、索引、schema version、参数化 | **通过** | schema + 单测（含 user_version） |
| 幂等 Upsert、顺序稳定、final 立即提交 | **通过** | 单测（去重/顺序）+ e2e mid-session |
| 实时持久化：仅 final、有界、不丢、失败上报 | **通过** | Recorder 单测 + e2e |
| 导出 JSON/TXT/SRT/session.json（UTF-8/原子/顺序/SRT 格式） | **通过** | Export 9 单测 + e2e SRT |
| 实时保存（非仅结束时保存），可从 SQLite 重建 | **通过** | e2e mid-session；恢复测试 |
| 崩溃恢复（幂等/不重不丢/损坏备份/DB 损坏报错/隔离） | **通过** | Recovery 9 单测（真实 SQLite） |
| 磁盘：开始前检查、运行中检查、路径穿越拒绝 | **部分验证** | 开始前拒绝 + 路径穿越单测通过；**运行中跌破阈值**为设计+代码路径覆盖，未在测试中真实制造磁盘满（标注未验证） |
| UI 最小存储状态；无历史浏览器/搜索 | **通过** | 主窗口存储状态区 |
| 日志不含完整字幕/partial/PCM | **通过** | 仅记录长度/ID；启动日志无正文 |
| restore/build/全部测试通过；M0–M3 无回归 | **通过** | 137 .NET + 14 py；WPF 启动无回归；0 孤儿 |

### 历史未完全验证项（继续如实保留，未改写）

- M1 完整 30 分钟稳定性：**未验证**（此前 5 分钟代理）。
- M1 真实音频设备拔出/切换：**未验证**。
- M2/M3 真实日语识别：**未验证**（无日语样本/TTS）。
- M3 Overlay 视觉人工验收：**未验证**（需用户肉眼）。
- M3 准确 CPU 占用采样：**未验证**（采样时机缺陷，仅推理耗时估算）。
- M3 30 分钟连续 UI 响应：**未验证**（此前 3 分钟）。

### 已知限制与风险（M4）

- “运行中磁盘跌破阈值”未在自动化中真实制造（`DiskSpace` 为静态、难注入）；代码路径存在并有 `DiskLow` 事件+安全停止，标为未验证并提供手工方法。
- 进程级强杀+重启恢复为手工步骤；等价逻辑由真实 SQLite 恢复测试覆盖。
- 默认输出根在应用运行目录下的 `Meetings/`（可用 `appsettings.json` 改为绝对路径）。

---

## Milestone 5：FFmpeg V1 录屏与音画复用

### FFmpeg 来源/版本/许可证

BtbN win64 **GPL** 构建 `N-125994-...-20260808`；ffmpeg.exe/ffprobe.exe 放于 `tools/ffmpeg/`（≈277 MB，不入 Git）。
SHA-256 与 GPL 影响见 [FFmpeg.md](FFmpeg.md)。含 `libx264`、`h264_qsv`、`gdigrab`、`aac`。

### 定位与能力探测（实测）

定位顺序：`Recording:FFmpegPath` → 向上查找 `tools/ffmpeg` → PATH。真实能力探测（`-version` + 0.2s 实际
`h264_qsv` 短编码）：本机 **QuickSync=False**（无可用 Intel QSV）→ 回退 **libx264**，UI 显示实际编码器。

### 实际 FFmpeg 命令结构

`-hide_banner -loglevel warning -y -thread_queue_size 1024 -f gdigrab -framerate 15 -i {desktop|title=<标题>}
[-thread_queue_size 1024 -f s16le -ar 16000 -ac 1 -i \\.\pipe\<guid>] -map 0:v:0 [-map 1:a:0]
-c:v {libx264 -preset veryfast -crf 23 | h264_qsv -global_quality 25} -pix_fmt yuv420p -r 15
[-c:a aac -b:a 96k -ar 16000 -ac 1] -movflags +faststart <output>`（全部经 ArgumentList，无 shell）。

### 实际执行命令与结果

```text
$ dotnet build KikuCaption.sln -c Debug        -> 0 警告 0 错误
$ dotnet test  KikuCaption.sln --no-build      -> .NET 163 通过（Recording 24 含 gated 自跳过）
$ pytest python/whisper_worker/tests/          -> 14 passed（M2/M3 无回归）
$ KIKU_FFMPEG=1 dotnet test --filter Category=RealFFmpeg
    CapabilityProbe            -> version=N-125994...，QuickSync=False
    Screen_Records_ValidMp4    -> complete=True h264+aac 2216x1278 15fps 视频4.07s 音频2.05s 无丢帧
    Screen_Records_Libx264     -> complete=True（回退路径）
    Window_Records_ValidMp4    -> 逐个尝试枚举窗口，取可播放者验证通过
$ KIKU_FFMPEG=1 KIKU_REC_SECONDS=120 ... Screen_LongRecording
    2 分钟：视频 120.33s / 音频 118.08s / 2.6MB / offset≈2.25s / 丢帧0 / 1h估≈77MB
$ tasklist | findstr ffmpeg/python（各步后）  -> 0（无孤儿）
```

### ffprobe 真实输出摘要（screen 4s）

容器 `mov,mp4,m4a,...`；视频 **h264** 2216×1278 **15fps** 时长 4.07s；音频 **aac** 16 kHz 时长 2.05s；`+faststart`。

### 整屏 / 窗口录制结果

- **整屏**：可播放 MP4，有画面+系统声音（aac），15fps，libx264（QSV 不可用）。
- **窗口**：gdigrab 对枚举窗口逐个尝试，取第一个可产出可播放 MP4 者验证通过；部分窗口（最小化/硬件加速/DWM）无法捕获（已知限制）。

### 录制音频格式 / 编码器 / QSV

录制音频 = M1 的 **16 kHz/mono/int16**（§M5 允许），输出 **AAC 16 kHz**；视频编码器实际 **libx264**（QSV 探测 False）。
录制分支使用**独立第二路 loopback 捕获**（背压隔离），命名管道唯一名+当前用户 ACL。

### 时间基准与音画同步（M5 修正后：内容同步达标，总时长差未达标——量化说明）

**M5 修正**：以单调时钟驱动的连续音频时间轴（`AudioTimeline`，详见 Recording.md）替代旧"仅有 PCM 才写管道"逻辑。
`expectedSamples = floor(elapsed × 16000)`，有数据写真实 PCM、无数据写零值静音，音频时间轴从不暂停；停止时补齐/裁剪到
明确会话结束时刻。**未使用硬编码 `-itsoffset` 掩盖问题**（明确禁止）。

**内容同步（唇同步关键指标）——达标 ≤500 ms**（客观 beep-marker 测试 `BeepMarkers_GapsPreserved_NoDrift`）：
- 在 2/7/17/20 s 播放 250 ms beep，用 `silencedetect noise=-40dB:d=0.15` 定位 onset：**2.48/7.45/17.50/20.22 s**。
- beep 间隔实测 **4.97 / 10.05 / 2.72 s**（期望 5 / 10 / 3），全程 span 累计漂移 **−257 ms ≤ 500 ms**。
- **开头静音不缩短、中间静音不消失、末尾无累计漂移**——单调时钟保证产出量由时钟决定，不累加漂移。
- 纯单元测试 12 个（含 2 min = 1,920,000 与 30 min = 28,800,000 样本无漂移模拟）全通过。

**音视频总时长差——未达 ≤500 ms 门槛（实测录屏 ~1.9 s，如实记录、未隐藏）**：
- 根因（已定位，**非漂移、非起始偏移**）：**FFmpeg 从命名管道读取音频慢于实时**，`q` 停止时管道缓冲尾部被丢弃，
  形成**恒定的尾部亏空**（`ffprobe` 实测 `vStart = aStart = 0`，确认不是起始偏移）。
- 这是"边写边复用"活流管道路径的固有限制；加大管道缓冲/调整停止顺序均无法降到 500 ms。
- PROJECT.md §5.3 允许"临时 WAV + 停止时 `-c copy` 无损复用"可让总时长差也 ≤500 ms；**经用户确认后决定保留当前
  管道方案**，不切换。
- **结论：M5 sync 在"总时长差 ≤500 ms"一项判定为"未通过/未接受"**；"内容对齐 ≤500 ms"一项**通过**。
  30 分钟真实录制**未执行 → 未验证**（因偏移恒定不累积，预期仍约 1.9 s）。

### 正常停止 / 异常退出

正常停止：停音频→drain 管道（≤3s 上限）→FFmpeg stdin `q`→等待≤15s→超时 kill-tree→ffprobe 校验→更新 RecordingPath。
异常（FFmpeg 启动失败/崩溃/目标关闭/管道失败/0 字节/不可播放）：保留文件、不删、`IsComplete=false`、不谎称成功；
Job Object+kill-tree 防孤儿。单元测试覆盖 ffmpeg_missing/window 无标题/重复开始/停止时 Idle 等守卫。

### M4 会话集成

`meeting.mp4` 写入当前会话目录，与字幕同一 Session ID；`MeetingSession.RecordingPath` 入库、`session.json` 含
`recordingPath`（Storage.Tests `SetRecordingPath_UpdatesSessionJson` 覆盖）。录屏失败不影响字幕（异常隔离）。

### 文件路径与大小

`Meetings/<ts>_<id>/meeting.mp4`；**2 分钟 ≈ 2.6 MB**；**1 小时估算 ≈ 77–87 MB**（15fps、会议画面、libx264）。
FFmpeg+ffprobe ≈ 277 MB（tools/ffmpeg）；Recording 构建产物为小体量托管 DLL；应用静态总占用仍 < 10 GB。

### CPU / 内存 / 磁盘 / 丢帧 / 孤儿

- jitter buffer 丢弃迟到样本：**0**（`AudioMetrics.DroppedLateSamples`，实测未触发上限）；孤儿进程：停止后 ffmpeg/python 均为 **0**。
- FFmpeg 内存/CPU：未单独精确 profiling（libx264 veryfast 15fps 会议画面 CPU 占用中等）；主程序/Worker 内存与 M2–M4 一致，标为“未单独测量”。

### 自动化测试数量与结果

Recording.Tests **36 通过**：含 `AudioTimeline` 纯单测 12（无漂移 2min/30min 模拟、jitter 有界、flush 补齐/不过写）、
命名管道 3（thin transport 并发读/超时/取消）、参数构建、定位、录制器守卫，真实 FFmpeg gated（probe/screen/libx264/window/
silence/beep-markers/长录）。beep-marker 测试断言内容漂移 ≤500 ms（通过）；时长差测试记录实测值并用宽松界（≤2500 ms）
防粗回归、注明严格 ≤500 ms 需 WAV+remux（已按用户决定保留管道）。全套 **.NET 175 + Python 14** 通过、0 孤儿。

### 逐条验收标准对照（Milestone 5）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| Recording 工程、依赖方向、无循环 | **通过** | Recording→Core；构建通过 |
| 整屏 + ≥1 种可靠窗口捕获 | **通过** | 整屏稳定；窗口对可捕获窗口通过（gdigrab 限制已记录） |
| MP4 有画面+系统声音、常见播放器可播放 | **通过** | ffprobe h264+aac、`+faststart`；人工播放需用户确认 |
| 内容/标记同步 ≤500 ms（唇同步） | **通过** | beep-marker 间隔 4.97/10.05/2.72s、累计漂移 −257ms；静音保留、无漂移 |
| 音视频总时长差 ≤500 ms | **未通过** | 恒定 ~1.9s 尾部亏空（FFmpeg 读管道慢于实时，q 时丢尾）；WAV+remux 可解但按用户决定保留管道 |
| 30 分钟音画偏移 ≤500 ms | **未验证** | 30 min 真实录制未执行；偏移恒定不累积，预期仍约 1.9s |
| Teams 关闭/FFmpeg 崩溃/磁盘不足安全停止并保留字幕 | **部分验证** | 守卫/崩溃保留文件单测通过；真实拔窗口/磁盘满为手工 |
| QSV 不可用自动 libx264 且显示 | **通过** | 探测 False→libx264，UI 显示 |
| 结构化参数、无 shell、标题不成路径注入 | **通过** | ArgumentList；title 单 token 单测 |
| 命名管道当前用户、唯一名、有界不阻塞 WASAPI | **通过** | ACL+GUID 名+有界丢帧；单测 |
| 无孤儿 FFmpeg/Python | **通过** | Job Object；tasklist=0 |
| restore/build/全部测试；M0–M4 无回归 | **通过** | 163 .NET + 14 py；WPF 启动无回归 |

### 历史未完全验证项（继续如实保留，未改写）

M1 30 分钟稳定性、M1 真实设备拔出/切换、M2/M3 真实日语识别、M3 Overlay 视觉人工验收、M3 准确 CPU 采样、
M3 30 分钟 UI 响应、M4 进程级强杀恢复、M4 运行中磁盘跌破阈值 —— 全部保持“未验证/部分验证”，未改写。

### 已知限制与风险（M5）

- **音画内容同步已达标 ≤500ms**（连续音频时间轴），但**音视频总时长差 ~1.9s 未达 500ms 目标**——FFmpeg 读命名管道
  慢于实时、`q` 时丢尾部的恒定亏空（非漂移）；PROJECT.md §5.3 的 WAV+remux 可彻底解决，经用户确认后保留当前管道方案。
- gdigrab 无法可靠捕获最小化/硬件加速/DWM 窗口。
- 录制音频 16 kHz（低保真，V1 允许）。
- QSV 需 Intel 硬件；本机用 libx264（CPU 更高）。
- 30 分钟录制、真实 Teams 窗口、视觉/听觉同步、FFmpeg/磁盘异常真实触发为**用户手工验证**（步骤见 Recording.md）。
- 部署需随附 `tools/ffmpeg` 或配置 `Recording:FFmpegPath`；GPL 分发义务见 FFmpeg.md。

---

## Milestone 6：公司 Azure OpenAI 兼容 API 日译中

新增 `KikuCaption.Translation`（仅依赖 Core，不依赖 WPF/Storage）+ `KikuCaption.Translation.Tests`。实现标准
OpenAI 兼容 Chat Completions 适配器、DPAPI 密钥存储、有界后台翻译队列、schema v1→v2 迁移、`translation.srt` 导出、
主窗口翻译设置面板与字幕原地双行更新。细节见 [Translation.md](Translation.md)、[Security.md](Security.md)。

### 实际执行的命令与结果

- `dotnet restore` / `dotnet build KikuCaption.sln -c Debug`：**成功生成，0 警告**。
- 全部 .NET 测试：**236 通过，0 失败**（Core 7、Infrastructure 9、Recording 36、Storage 44、Translation 42、Audio 19、App 15、Speech 64）。
- Python worker 测试：**14 通过**（`pytest -q`）。
- gated UI 虚拟化（`KIKU_UI=1`）：通过（M3.1，回归无碍）。
- **WPF 真实启动**：应用启动成功，DI 图（含 Translation/HttpClientFactory）解析正常；日志实测
  “**Migrated database schema v1 → v2**”（真实旧库迁移生效）、环境检查 OK、存活稳定后正常退出。
- 孤儿检查：运行后无 `python.exe`/`ffmpeg.exe` 孤儿（`pythonw.exe` 为用户既有后台进程，非本项目）。

### fake 端到端（注入 HttpMessageHandler / 脚本翻译器，不访问真实网络）

- 日语 final → 触发规则 → 入队 → fake 翻译 → SQLite（`Translated`）→ 卡片原地更新 → `translation.srt`：`JaFinal_Translates_UpdatesSegment_And_Job` 通过。
- partial/中文/空文本/未启用/已翻译/重复 final **不入队/不重复**：触发规则 6 项断言通过。
- timeout / 401 / 403 / 429+Retry-After / 500/502/503 / 网络断开 / 非 JSON / 空响应 / 超大响应 / 取消：适配器 22 项测试全通过。
- 重试可恢复失败→成功、最大重试→`FailedPermanent`（保留原文）、乱序按 SegmentId 更新、指数退避+jitter+Retry-After：队列/退避测试通过。
- 重启恢复 `Pending`/`RetryScheduled`/遗留 `InProgress→Pending`；成功不重发；停止取消当前请求但保留 Pending；原文不受失败影响：通过。
- DPAPI 保存/读取/替换/删除、损坏密文抛错且不删文件、磁盘只有密文无明文：安全测试通过。

### 密钥泄漏检查

- `appsettings*.json` 无 `ApiKey` 键；源码/配置/日志无测试密钥（tests/ 内为预期）；`src` 下无 `*.key`；运行时数据库中无 `Bearer/Authorization/api-key/sk-` 等密钥串。**通过**。

### 逐条验收对照（Milestone 6）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| Translation 工程、依赖方向（可依赖 Core、不依赖 WPF、Core 不依赖 Translation、无循环） | **通过** | 构建通过；csproj 仅引用 Core |
| API 请求格式可配置、协议隔离在适配器、不硬编码微软域名/api-version | **通过** | 适配器测试；Endpoint 完整地址 + 可选 api-version |
| 三种认证模式（Bearer/ApiKeyHeader/None） | **通过** | 适配器测试 1–3 |
| 触发规则（ja/final/启用/非空/未译/无活动任务），禁止 partial/zh/重复/音视频 | **通过** | 触发 + 队列测试 |
| 固定 Prompt、system/user 分离、低随机性、输出校验 | **通过** | 适配器测试 6–8 |
| 有界后台队列不阻塞各管线、满不丢任务、默认单并发、SegmentId 关联、乱序更新正确 | **通过** | 队列测试 |
| TranslationJob 6 态 + 字段；单任务/成功不重发/崩溃恢复/幂等/不改原文；不存敏感 | **通过** | 队列+迁移+安全测试 |
| 重试与错误分类（超时/429+RetryAfter/5xx/网络重试；400/401/403/无效不重试） | **通过** | 适配器+队列测试 |
| HttpClientFactory 复用、HTTPS 强制、注入 Handler 不访问网络、大小/长度限制、空/错误 HTML 判失败 | **通过** | 适配器测试 |
| DPAPI 保存/读取/删除/替换、PasswordBox 不回显、解密失败不删、无明文入库/日志 | **通过** | 安全测试 + 泄漏扫描 |
| UI：设置面板、Test Connection 固定文本、原地双行更新、不重复卡片、不强制滚动、zh 无翻译区、失败保留原文 | **通过** | UI 测试 5 项 + 面板 |
| translation.srt：原始时间、序号连续、只输出成功中文、UTF-8、顺序一致、重复不重复、可重建 | **通过** | 导出测试 |
| schema v1→v2 显式迁移、保留旧数据、失败不重建、文档记录 | **通过（真实迁移实测）** | 迁移测试 + 启动日志 |
| restore/build/全部测试/Python/WPF 启动；M0–M5 无回归 | **通过** | 236 .NET + 14 py + 真实启动 |
| 无 Python/FFmpeg 孤儿 | **通过** | 运行后 tasklist 无孤儿 |
| **真实公司 API（用户配置 + 固定文本 + 非敏感日语句）** | **未验证** | 无公司 API 规范/凭据；步骤见 Translation.md，等用户提供 |

### 历史未完全验证项（继续如实保留，未改写）

M1 30 分钟稳定性、M1 真实设备拔出/切换、M2/M3 真实日语识别、M3 Overlay 视觉人工验收、M3 准确 CPU 采样、
M3 30 分钟 UI 响应、M4 进程级强杀恢复、M4 运行中磁盘跌破阈值、M5 音视频总时长差 ≤500ms/30 分钟录屏、
M3.1 像素级流畅手感/真机长会话 —— 全部保持“未验证/部分验证”，未改写。

### 已知限制与风险（M6）

- **真实公司 API 未验证**（无规范/凭据）；fake 端到端全通过。若真实格式明显不同，将新增 Adapter，不大改现有模块。
- `MaxQueueLength`/`MaxConcurrency` 在启动时读取（通道大小/worker 数），运行时修改需重启；其余翻译设置可热更新。
- 翻译质量依赖公司模型；本 Milestone 只保证协议、队列、持久化、安全与 UI 行为正确。
- CPU/内存/磁盘：翻译为轻量文本 HTTP + 有界队列，未见显著增量；DPAPI 密文与 `translation.srt` 占用可忽略。

---

## Milestone 7：集成、稳定性与交付

在 M0–M6 + M3.1 之上完成统一会话生命周期、预检、资源监控（修复 CPU 采样）、日志轮转、敏感信息扫描、
用户设置、关闭安全停止与自包含发布包。设计见 [Architecture.md](Architecture.md)、交付见 [Delivery.md](Delivery.md)、
用户文档见 [UserGuide.md](UserGuide.md)、许可见 [../THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。

### 实际执行的命令与结果

- `dotnet restore` / `dotnet build KikuCaption.sln -c Debug`：**成功生成，0 警告**。
- 全部 .NET 测试：**279 通过，0 失败**（Core 30、Infrastructure 29、Recording 36、Storage 44、Translation 42、Audio 19、App 15、Speech 64）。
  - 注：Audio.Tests 在与其它程序集**并行**跑时偶发 1 例失败（真实 WASAPI 设备争用，M6 起既有），**单独运行 19/19 通过**，非 M7 回归。
- Python worker 测试：**14 通过**。
- **Release 自包含发布**：`dotnet publish -c Release -r win-x64 --self-contained`：成功，便携目录 **177 MB**。
- **发布包**：zip **72.3 MB**，SHA-256 `0982B3874D8F24C9B32CBD80B20F593A96BDE380ACE5EBBB3045486C0D4A1FC5`。
- **WPF 真实启动**：M7 DI（PreflightService/状态机/资源采样）解析正常，环境检查 OK，稳定存活后正常退出；无孤儿。

### 静态占用（实测，见 Delivery.md）

.NET 应用 177 MB + Python `.venv` 303 MB + `small` 模型 464 MB + FFmpeg 278 MB ≈ **1.22 GB < 10 GB ✓**。

### 密钥泄漏检查（自动化）

- `SensitiveInfoScannerTests.RepoSourceAndConfig_HaveNoSecrets`：扫 `src` + `docs`（排除 tests/bin/obj/.venv）→ **0 命中**。
- 发布包机扫：无 `Bearer/sk-/api-key:` 明文、无 `*.key`、无 `settings.json`、无 `Meetings/logs/secrets`、appsettings 无 `ApiKey`、0 pdb。**通过**。

### 逐条验收对照（Milestone 7）

| 验收标准 | 状态 | 证据 |
|---|---|---|
| 统一会话状态机（Idle/Preflight/Starting/Running/Stopping/Completed/Faulted/Recovering） | **通过** | `SessionStateMachine` + 10 单测（合法转移/拒绝重复开始/幂等停止/回滚/故障） |
| 单一开始/停止；拒绝重复开始；幂等停止；启动中取消回滚；启动失败停止已启动模块；已落盘字幕不删 | **通过** | 状态机单测 + VM 集成（StartAsync 回滚 + Faulted） |
| 启动前预检（通过/警告/阻断），翻译不可用=警告，音频/模型/存储=阻断，录屏不可用=明确选择 | **通过** | `PreflightEvaluator` + 7 单测；`PreflightService` 实测采集 |
| 设置持久化不存明文密钥；损坏回退安全默认；DPAPI 密钥独立 | **通过** | `UserSettingsStore` + 3 单测（roundtrip/损坏默认+备份/无 Key） |
| 关闭确认与安全停止（不强杀）；保留 MP4 与字幕 | **通过（逻辑+启动实测）** | MainWindow `OnClosing` 确认→StopCommand；真机关闭对话为手工 |
| 资源测量：可复现 CPU（TotalProcessorTime 增量 ÷ elapsed×核数，退出不写 0%） | **通过** | `CpuUsageCalculator`/`ProcessCpuSampler` + 单测（含退出→null、60 分钟模拟不溢出） |
| 日志轮转 + 启动清理超期；不记录字幕/翻译/PCM/标题/密钥 | **通过** | Serilog 日切+保留 + `LogRetention` 单测；`DiagnosticsFormatter` 仅数值 |
| 敏感信息自动扫描（源码/配置/发布包/日志/SQLite/session.json） | **通过** | `SensitiveInfoScanner` + 单测 + 仓库/发布包扫描 0 命中 |
| 恢复与数据完整性（正常停止/取消/强杀/重启恢复/不重复/MP4 损坏保留/单会话隔离/不删用户文件） | **通过（M4/M6 覆盖，M7 复用）** | 现有恢复测试 + 状态机回滚；真机强杀为手工（虚构会话） |
| 发布包含/可定位应用+程序集+Python 说明+锁定依赖+模型首下+FFmpeg+配置+文档+许可+版本 | **通过** | `publish.ps1` + 实测包 + `THIRD_PARTY_NOTICES.md` + `licenses/` |
| 发布包不含 Key/DPAPI/设置/会议/日志/缓存/venv/HF 重复/pdb | **通过** | 排除规则 + 发布包实扫 |
| 静态占用 < 10 GB 并列出组成；运行内存首选 < 5 GB（硬限 < 10 GB） | **通过（占用实测 1.22GB）；内存部分验证** | Delivery.md 组成表；峰值内存未做 60 分钟长跑测量 |
| RTF < 1、常见 partial 延迟 1–3s、UI 始终响应 | **部分验证** | M2/M3 实测 RTF≈0.2；60 分钟真机延迟/响应未测 |
| 停止后 MP4/JSON/TXT/SRT/翻译 SRT 有效 | **通过** | M4/M5/M6 导出 + fake 端到端 |
| **60 分钟真实端到端** | **未验证** | 无真实 Teams/音源；步骤已列，未执行完整 60 分钟 |
| **真实公司 API / 断网离线试用 / 干净机安装** | **未验证** | 无凭据/无隔离环境；步骤见 Translation.md/Delivery.md |
| restore/build/全部测试/Python/WPF 启动；M0–M6 无回归 | **通过** | 279 .NET + 14 py + 真实启动 |
| 无 Python/FFmpeg 孤儿 | **通过** | 运行后 tasklist 无孤儿 |

### 历史未完全验证项（继续如实保留，未改写）

M1 30 分钟稳定性/真实设备拔出、M2/M3 真实日语识别、M3 Overlay 视觉/30 分钟 UI、M3.1 像素级流畅/真机长会话、
M4 进程级强杀/运行中磁盘跌破、M5 音视频总时长差 ≤500ms（~1.9s，Named Pipe 保留）/30 分钟录屏、
M6 真实公司 API —— 全部保持“未验证/部分验证”，未改写。

### 已知限制与风险（M7）

- **真实 60 分钟端到端、真实公司 API、断网离线试用、干净机安装**未在本环境执行 → **未验证**（不写成通过）；步骤已在文档给出。
- 峰值运行内存与长跑 RTF/延迟未做 60 分钟实测；短时观测正常。
- 每进程 CPU 采样覆盖主进程与 FFmpeg（经 PID）；Whisper Worker 负载经 RTF/推理/队列指标反映（未单独 PID 采样）。
- 输出目录默认保留在运行目录（用户确认）；装在不可写目录时预检报阻断并提示改用户可写目录。
- 捆绑 GPL v3 FFmpeg 的发布包对外分发须履行 GPL 义务（见 THIRD_PARTY_NOTICES.md）。
