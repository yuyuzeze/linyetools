# 可切换识别后端：speech.cpp 设计说明（Step 1）

> 目标：在「设置」里选择识别**引擎 + 模型**，实时字幕与文件识别据此走不同后端。
> 现状引擎 = Whisper（Python faster-whisper worker）。新增引擎 = SpeechCpp（`speech.exe`，FastConformer/CTC，非自回归）。
> **本文档只做协议映射设计与改动清单，不写实现代码。** 实测事实见文末来源。

---

## 1. 两个 worker 协议对照（已核对 docs/worker.md，protocol v4）

| 维度 | Whisper（现有 Python worker） | speech.cpp（`speech worker <MODEL>`） |
|---|---|---|
| 启动 | `python main.py [--download-root DIR]` | `speech.exe worker <MODEL> [--device NAME] [--threads N] [请求选项…]` |
| 就绪 | `{"type":"ready","modelLoadMs":…}` | `{"type":"ready","protocol":4,"version":…,"model":{…}}`；失败 `{"type":"fatal","error":{…}}` 并退出(1/2) |
| 初始化传参 | Initialize 消息带 model/device/compute/beam/language/initialPrompt/hotwords | 全部在**命令行**（模型名 + --device/--threads + 默认请求选项）；逐请求可覆盖 |
| 送音频 | `audio` 消息（base64）× N → `flush` | `chunk` 行 `{"type":"chunk","id","seq":0,1,…,"pcm"}` → `transcribe` 行 |
| 一次识别 | flush 后流式 `partial`×N + `final_candidate`×N + `flushed` | `transcribe` 后：可选 `progress` → **恰好一个**终止消息 `end`/`error`/`cancelled` |
| 结果 | 每条 partial/final 带 text/start/end/confidence | `end` 带 `text`、`stop`，`timestamps` 时带 `segments[]`/`tokens[]` |
| PCM | 16k mono s16le，base64 | **完全相同**：base64 16-bit LE mono，`x/32768`；`sample_rate` 任意（库自动重采样） |
| 并发 | 串行 | 「请求一次一个」，按 `id` 区分；需为每次识别用新 `id` |
| 取消 | 关流 | `{"type":"cancel","id"}` → `cancelled` |

**关键差异：speech.cpp 的识别是「按整句请求/应答」，不在一次 transcribe 内吐 partial。**
FastConformer「一次识别一段音频，含多句的长音频会丢整句，所以一个请求应是一个 utterance」。

---

## 2. 映射到 KikuCaption 的 ISpeechRecognizer 契约

对外契约必须与 `PythonSpeechRecognizer` 完全一致，这样 `RealtimeCaptionPipeline` 和 `SpeechViewModel` 内部逻辑**不改**：

```
Task InitializeAsync(SpeechOptions, ct)
IAsyncEnumerable<TranscriptUpdate> RecognizeAsync(IAsyncEnumerable<AudioChunk>, ct)
  // 产出 TranscriptUpdate{ Kind=FinalCandidate, Text, StartTime, EndTime }，流结束即本次识别完成
```

消费侧已知行为（已核对）：
- `RealtimeCaptionPipeline.TranscribeAsync` **只把所有 `FinalCandidate.Text` 拼接**，忽略 partial；流结束即一轮完成。管线自带整句缓冲与时间戳，不依赖 worker 的 start/end。
- `SpeechViewModel.RecognizeWavAsync` **逐条 FinalCandidate 打印 `[start–end] text`**，用到时间戳。

### SpeechCppSpeechRecognizer 的映射方案

- **InitializeAsync**：
  1. 定位 `speech.exe`（见 §4）；启动 `speech worker <model> --device <cpu/auto> [--threads N]`。
  2. 读首行，等 `ready`（带超时，复用 `SpeechOptions.InitializeTimeout`）。`fatal` → 抛 `SpeechRecognitionException`（见 §5 回退）。
  3. worker 常驻、模型只加载一次（与现状模式一致，天然适配预热）。

- **RecognizeAsync(audio)** = **一次 speech.cpp 识别请求**：
  1. 取新 `id`（自增或 Guid）。
  2. 遍历 `audio`：每个 `AudioChunk` → 一条 `chunk`（base64 PCM，`seq` 自增）。PCM 格式与现状一致，直接复用。
  3. 音频枚举结束 → 发 `transcribe {id, sample_rate:16000, <选项>}`。
  4. 读该 `id` 的消息：`progress` 忽略（或只更新指标）；`end` → 产出 FinalCandidate（文本映射见下）→ **完成流**；`error`/`cancelled` → 抛异常/结束。
  5. ct 取消 → 发 `cancel {id}`。

- **文本→FinalCandidate 的两种模式（需你定，见 §6 决策）**：
  - **实时**：请求 `keywords`（Haas 自定义词汇，来自词典 hotwords），**不请求 timestamps**；`end.text` 作为**一条** FinalCandidate（管线只拼文本，时间戳自管）。
  - **文件**：请求 `timestamps=true`，把 `end.segments[]` **逐段**映射为 FinalCandidate（`segment.start/end/text`），供 SpeechViewModel 显示分段时间。
  - ⚠️ Haas 限制：`keywords` **不能**与 `timestamps` 同用。故实时用 keywords、文件用 timestamps，二者走不同请求选项。模式经 `SpeechOptions`（purpose/新字段）传入。

映射对等性结论：**一条或多条 FinalCandidate + 流结束** 同时满足实时（拼接）与文件（逐段）两条消费路径，`RealtimeCaptionPipeline` 零改动。

---

## 3. 引擎/模型选择与配置流

- 新增 `SpeechEngine` 概念：`Whisper` | `SpeechCpp`。`SpeechSettings.Engine` 字段已存在（现为 "Whisper"），复用并扩展取值。
- `SpeechOptions` 增加 `Engine`（+ 可选 SpeechCpp 专属：模型名、keywords、是否 timestamps）。
- `SpeechOptionsProvider.ForLanguage(lang, purpose)` 依据所选引擎填 `Engine` 与对应模型；Whisper 分支保持原样。
- 选择器/工厂按 `SpeechOptions.Engine` 返回对应 `ISpeechRecognizer`；`SpeechRecognizerPrewarmer.Equivalent` 增加 `Engine` 比较（否则切换引擎后预热缓存不失效）。

当前 DI（`SpeechServiceCollectionExtensions`）：`Func<ISpeechRecognizer>` 恒返回 `PythonSpeechRecognizer`。
改为：注册两种识别器（transient）+ 一个 `ISpeechRecognizerFactory.Create(SpeechOptions)` 按 `Engine` 分发。预热器与 `SpeechViewModel` 改用该工厂。

---

## 4. 定位与缺失处理

- `speech.exe`：优先固定路径 `%LOCALAPPDATA%\Programs\speech.cpp\current\speech.exe`，否则 PATH；找不到 → 见 §5。
- 模型：命令行只传模型名，`speech worker` 自己从其模型库解析；缺模型会回 `fatal{code:model_file}`。
- 下载：提示用户 `speech pull <model>`（**下载需用户确认**，不自动拉）。
- 候选模型：`haas-ja-v1.0`（ja，带标点，CER 4.4%，0.66GB，支持 keywords）、`reazonspeech-v2`、`parakeet-tdt_ctc-0.6b-ja`。

---

## 5. 回退（必须不崩）

`speech.exe` 或模型缺失、worker `fatal`/启动失败时：
- 给出**可读错误**（三语），并**回退到 Whisper**（即工厂在 SpeechCpp 不可用时产出 `PythonSpeechRecognizer`，或 UI 阻止选择并提示）。
- 绝不让程序崩溃；日志只记错误码/类别，不记字幕文本/prompt/keywords/密钥。

---

## 6. 需你拍板的决策

1. **Haas：timestamps 还是 keywords？** 建议：实时=keywords（短句+词汇增强）、文件=timestamps（分段时间）。确认？
2. **回退策略**：SpeechCpp 不可用时，是「静默回退 Whisper + 提示」还是「禁止选择并引导下载」？建议前者（更稳）。
3. **模型下载**：是否在「模型管理页(R7B)」加 speech.cpp 模型条目并走 `speech pull`（需确认）？还是仅在设置页给一键下载按钮？
4. **默认引擎**：保持 Whisper 为默认（不改现有用户行为），SpeechCpp 为可选。确认？

---

## 7. 改动清单（文件级，待你确认后按阶段实现）

**新增**
- `src/KikuCaption.Speech/Worker/SpeechCppWorkerProcess.cs` —— 启动 `speech.exe worker`，JSON Lines 收发 + stderr + Job Object（可抽取 `ProcessWhisperWorker` 的进程/清理逻辑为共享基类）。
- `src/KikuCaption.Speech/Worker/SpeechCppSpeechRecognizer.cs` —— `ISpeechRecognizer` 实现，§2 映射。
- `src/KikuCaption.Speech/Worker/SpeechCpp*` 协议编解码（chunk/transcribe/ready/end 的 DTO + JsonLines）。
- `src/KikuCaption.App/Services/SpeechCppLocator.cs` —— 定位 speech.exe、检测模型、生成 `speech pull` 命令。
- 工厂：`ISpeechRecognizerFactory` + 实现（按 `SpeechOptions.Engine` 分发）。
- 测试：假 worker 的 `SpeechCppSpeechRecognizer` 单测（chunk/transcribe→end 映射、error、cancel）、工厂选择、回退。

**修改**
- `src/KikuCaption.Core/Models/SpeechOptions.cs` —— 加 `Engine`（+ SpeechCpp 专属字段）。
- `src/KikuCaption.Core/Models/SpeechOptionsProvider.cs` —— 按引擎填 `Engine`/模型。
- `src/KikuCaption.Speech/Worker/SpeechRecognizerPrewarmer.cs` —— `Equivalent` 加 `Engine`。
- `src/KikuCaption.Speech/DependencyInjection/SpeechServiceCollectionExtensions.cs` —— 注册两后端 + 工厂。
- `src/KikuCaption.Infrastructure/Configuration/UserSettings.cs` —— 加 `SpeechEngine`、`SpeechCppModel`。
- `src/KikuCaption.Infrastructure/Configuration/AppSettings.cs` + `appsettings.json` —— `SpeechSettings` 引擎/模型默认值。
- `src/KikuCaption.App/ViewModels/SpeechViewModel.cs` —— 文件识别改用工厂（按引擎）。
- 设置 UI（设置页 + ViewModel）—— 引擎+模型下拉；三语文案（zh-CN/en-US/ja-JP 的 .resx 各一份）；持久化 UserSettings；切换后下次识别生效（或提示重启识别）；预热 `SpeechPrewarmCoordinator` 适配新后端。

**铁律（遵守）**：不改 PROJECT.md；未经允许不 git add/commit/push；不破坏 RealtimeCaptionPipeline 的整句缓冲/精确移除不变量；新 UI 文案三语齐全；不动 venv/模型/dictionaries.json/用户设置（未经同意）；日志不含敏感内容；现有测试保持绿，新代码加单测。

---

## 8. 建议阶段（每阶段可独立验证）

1. **本设计**（当前）→ 你确认 §6 决策。
2. `SpeechCppWorkerProcess` + `SpeechCppSpeechRecognizer` + 协议 DTO + 工厂 + 配置项（**先不接 UI**，用单测/命令行验证 chunk→transcribe→end 映射）。
3. 接实时管线 + 文件识别，端到端跑通（日志 `LastInferenceMs` 对比 Whisper vs haas-ja 短句延迟）。
4. 设置 UI（三语）+ 持久化 + 预热 + 缺失回退。
5. 补测试、延迟自测、整理改动说明（等你确认后再提交）。

---

## 本机实测（2026-11-15，CPU，同一会议音频的短クリップ）

| クリップ | Whisper small(beam2) | haas-ja(CTC) | 倍率 |
|------|------|------|------|
| 3s | 1304 ms | **170 ms** | 7.7× |
| 6s | 1355 ms | **313 ms** | 4.3× |
| 10s | 1542 ms | **539 ms** | 2.9× |
| 20s（難しい窓） | **18〜23 秒 固まる** | **1266 ms** | 18.5× |
| モデルロード | 1.7 s | 0.76 s | — |

- Whisper は 30 秒パディングの ~1.3s 床があり、難しい窓では温度フォールバックで 20 秒級に暴走（= 既知の「卡住」）。VAD on で 1.9s に回復。
- haas は音声長に線形（RTF 0.05〜0.06）、テール遅延の暴走なし。実時間字幕で最も効く。
- 確認済み実フィールド：`ready{type,protocol:4,version,defaults,model{sample_rate,languages,options,…}}`；`end{type,id,text,stop}`（`timestamps` 時のみ `segments`/`tokens`）；`keywords` オプション動作OK；haas-ja は **DL 済み**（pull 不要）。

## 来源（本机已核对）
- `C:\Users\yuyuzeze\Desktop\voice\speech.cpp\docs\worker.md`（协议 v4：ready/chunk/transcribe/end）
- `…\docs\cli.md`（`speech worker/asr/pull`、`--vad`、`--keywords`、`--timestamps`、`--device/--threads`）
- `…\docs\models\fastconformer.md`（haas-ja-v1.0：ja+标点、CER 4.4%、keywords 与 timestamps 不可同用、一次一 utterance）
- `speech.exe`：`%LOCALAPPDATA%\Programs\speech.cpp\current\speech.exe`（已存在）
- KikuCaption 侧：`ISpeechRecognizer`、`PythonSpeechRecognizer`、`SpeechRecognizerPrewarmer`、`SpeechOptions(Provider)`、`SpeechServiceCollectionExtensions`、`SpeechViewModel`、`UserSettings`、`AppSettings`（均已读）
