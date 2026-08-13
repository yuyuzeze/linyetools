# 第三方依赖与许可证（Third-Party Notices）

KikuCaption 使用以下第三方组件。各组件版权归其作者所有，按其各自许可证提供。本文件为发布随附声明
（Milestone 7 §11）。⚠ **重要许可证提示见文末「FFmpeg 与分发义务」。**

## .NET / NuGet 依赖

| 组件 | 版本 | 许可证 |
|---|---|---|
| .NET 10 运行时（自包含发布） | 10.0.x | MIT（.NET Runtime / Libraries） |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| NAudio | 2.2.1 | MIT |
| LibVLCSharp.WPF | 3.10.0 | LGPL-2.1-or-later |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | LGPL-2.1-or-later (native LibVLC runtime) |
| Microsoft.Data.Sqlite | 10.0.10 | MIT |
| SQLitePCLRaw.lib.e_sqlite3 | 3.50.3 | Apache-2.0（SQLite 本体属公有领域 Public Domain） |
| Microsoft.Extensions.*（Hosting/DI/Config/Options/Logging/Http） | 10.0.0 | MIT |
| Serilog / Serilog.Sinks.File / Console / Extensions.Hosting | 4.2.0 / 6.0.0 / 6.0.0 / 9.0.0 | Apache-2.0 |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT |
| xUnit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 2.9.2 / 2.8.2 / 17.12.0 | Apache-2.0 / MIT（仅测试，不随用户包发布） |

## Python 运行时与依赖（Whisper Worker，approach A 由用户本机安装）

| 组件 | 版本 | 许可证 |
|---|---|---|
| CPython | 3.13.x | PSF License |
| faster-whisper | 1.2.1 | MIT |
| CTranslate2 | 4.8.1 | MIT |
| onnxruntime | 1.28.0 | MIT |
| av (PyAV) | 18.0.0 | BSD-3-Clause（内部封装 FFmpeg 库，见下） |
| huggingface_hub | 1.27.0 | Apache-2.0 |
| tokenizers | 0.23.1 | Apache-2.0 |
| numpy | 2.5.1 | BSD-3-Clause |
| httpx / httpcore / h11 / idna / certifi 等 | 见 `requirements-lock.txt` | 各自 MIT/BSD/MPL 等 |

完整锁定版本见 [`python/whisper_worker/requirements-lock.txt`](python/whisper_worker/requirements-lock.txt)。

> 注：PyAV 会加载 FFmpeg 家族的原生库。faster-whisper 的默认解码路径通常经 CTranslate2/onnxruntime，
> 不必然使用 PyAV；但如启用了经 PyAV 的音频解码，其 FFmpeg 库的许可证义务同样适用（多数 PyAV wheel 采用
> LGPL 构建的 FFmpeg；请以你实际安装的 wheel 为准）。

## 录屏用 FFmpeg（独立可执行文件）

| 组件 | 来源 | 许可证 |
|---|---|---|
| FFmpeg / ffprobe（BtbN Windows x64 构建） | https://github.com/BtbN/FFmpeg-Builds | **GPL v3**（该构建含 libx264 等 GPL 组件） |
| libx264（经 FFmpeg 调用） | — | **GPL v2+** |

版本、SHA 与来源见 [`docs/FFmpeg.md`](docs/FFmpeg.md)。二进制不提交 Git；由发布脚本从 `tools/ffmpeg` 打包或由用户配置路径。

## Whisper 模型

| 组件 | 许可证 |
|---|---|
| faster-whisper `small`（CTranslate2 转换权重，源自 OpenAI Whisper） | MIT（OpenAI Whisper 模型权重与代码） |

模型不随包发布（approach A），首次运行按需下载并做完整性校验（见 [`docs/UserGuide.md`](docs/UserGuide.md)）。

## FFmpeg 与分发义务（务必阅读）

- 本项目随附/使用的 **FFmpeg 为 GPL v3 构建**（含 libx264）。**个人本机使用**通常不产生对外分发义务。
- **对外分发**（把包含该 FFmpeg 构建的发布包给他人）时，你必须履行 **GPL v3** 义务：随附相应源码或书面提供源码的
  offer、保留版权与许可证声明、以相同许可证条款分发相应部分。
- **不得**声称 KikuCaption「整体」以与 GPL 冲突的许可证发布：一旦捆绑 GPL 的 FFmpeg 并对外分发，该组合分发受
  GPL 约束。若需避免 GPL，请改用 LGPL 构建的 FFmpeg 或不捆绑 FFmpeg（由用户自备）。
- 如仅分发 .NET 应用与脚本、而 FFmpeg 由用户自行获取，则 .NET 部分按其 MIT/Apache 许可证分发，FFmpeg 的义务由
  用户获取时承担。

各许可证全文与获取方式见 [`licenses/`](licenses/README.md)。
