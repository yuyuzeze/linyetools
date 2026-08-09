# 交付与发布（Milestone 7）

## 发布方案：A vs B

| 方案 | 说明 | 取舍 |
|---|---|---|
| **A（本项目采用）** | 自包含 .NET 应用 + 脚本化本机 Python（`scripts/setup-python.ps1`），模型首次运行下载 | 包体小、可靠、许可清晰；需用户一次性建 venv + 首次联网下模型 |
| B | 项目自带完整 Python 运行时与依赖的便携版 | 更接近开箱即用；包体大；需处理 Python/CTranslate2/onnxruntime/PyAV 的原生加载与许可；**须在干净机验证** |

**采用 A 的理由**：在本开发环境无法可靠验证 B 的「自带 Python 运行时 + CTranslate2/onnxruntime/PyAV 原生库离线加载」
（须干净机验证）。A 的每一步（自包含 .NET 构建、启动、占用）均可实测，许可义务清晰。B 标记为**未验证**：若未来在
干净机验证通过其原生加载与离线运行，可再切换为便携版。

## 静态硬盘占用（实测，approach A）

| 组成 | 实测 | PROJECT.md §14.1 预算 |
|---|---:|---:|
| .NET 自包含应用（win-x64，Release，无 pdb） | **177 MB** | ≤ 0.5 GB |
| Python 运行环境 + 锁定依赖（`.venv`） | **303 MB** | ≤ 2.5 GB |
| faster-whisper `small` 模型（首次下载） | **464 MB** | ≤ 1.5 GB |
| FFmpeg + ffprobe（BtbN GPL v3） | **278 MB** | ≤ 0.3 GB |
| **合计（含模型）** | **≈ 1.22 GB** | **< 10 GB ✓** |

会议 MP4/字幕/日志属**用户数据**，不计入静态占用（PROJECT.md §12）。

## 发布包（可复现）

- 脚本：[`scripts/publish.ps1`](../scripts/publish.ps1)（自包含 win-x64 + 排除规则 + zip + SHA-256）。
- 便携目录：`KikuCaption-0.1.0-win-x64/`（**177 MB**）。
- 压缩包：`KikuCaption-0.1.0-win-x64.zip`（**72.3 MB**）。
- **SHA-256**：`0982B3874D8F24C9B32CBD80B20F593A96BDE380ACE5EBBB3045486C0D4A1FC5`（随包附 `.sha256`）。

### 发布包**排除**（实测确认不含）

`*.pdb`、`secrets/`、`settings.json`、`Meetings/`、`logs/`、`.venv`、`models/`、`*.key`、Hugging Face 重复缓存、
`__pycache__`、`*.corrupt-*.bak`。发布包机扫无明文密钥、无 `ApiKey`、无用户数据（见 [Verification.md](Verification.md)）。

### 发布包**包含/可定位**

自包含 .NET 应用与必需程序集、`appsettings.json` 默认配置、`tools/ffmpeg`（若本机存在则打包，否则文档指引配置
`Recording:FFmpegPath`）、Python 脚本与**锁定依赖清单** `requirements-lock.txt`、模型**首次下载**说明、
`README.md` + `docs/` + `THIRD_PARTY_NOTICES.md` + `licenses/`、版本信息。

## 依赖版本清单

- .NET/NuGet：见 [`Directory.Packages.props`](../Directory.Packages.props) 与 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。
- Python：见 [`python/whisper_worker/requirements-lock.txt`](../python/whisper_worker/requirements-lock.txt)（faster-whisper 1.2.1、
  ctranslate2 4.8.1、onnxruntime 1.28.0、av 18.0.0、numpy 2.5.1、huggingface_hub 1.27.0、tokenizers 0.23.1 等）。
- FFmpeg：BtbN GPL v3，版本/SHA/来源见 [`docs/FFmpeg.md`](FFmpeg.md)。

## 离线运行

- **模型不随包发布**（A）：首次运行需联网下载 `small` 模型（约 464 MB），UI/日志给出下载与磁盘提示，并做完整性校验，
  下载完成后**可离线使用、不重复下载**。文档明确「首次运行需要网络」。
- 翻译**关闭**时完全本地，不进行任何联网。
- 若未来改为随包发布模型 + Python（方案 B），需在**断网/隔离**环境执行真正的离线验证（启动→加载模型→识别本地
  中文 WAV→保存 SQLite/JSON/TXT/SRT→录制本地桌面与测试音→MP4→正常停止→无孤儿）。本环境**未执行**该断网验证 →
  标记**未验证**，步骤见 [Verification.md](Verification.md)。

## 输出目录（保留运行目录默认 + 风险警告）

经用户确认，**保留**「输出目录默认位于应用运行目录」的既定行为（`Storage:OutputDirectory` 相对 `Meetings`）。

> ⚠ **风险**：若把应用装在**普通用户不可写**的目录（如 `C:\Program Files\...`），默认输出将无法写入。

缓解：
- **便携版请放在用户可写目录运行**（如 `%USERPROFILE%\Desktop\KikuCaption\` 或文档目录），此时默认输出可写。
- 或在设置中把 `Storage:OutputDirectory` 指向绝对的用户可写路径（如 `%USERPROFILE%\Documents\KikuCaption\Meetings`）。
- **预检**会检查输出目录可写性：不可写时报**阻断**并提示改用用户可写目录，绝不静默失败。

## 安装 / 运行 / 卸载

见 [UserGuide.md](UserGuide.md)。卸载默认**不删除** `Meetings/`、SQLite 数据库、DPAPI 密钥与用户设置；如需删除，
文档列出精确路径由用户主动操作。
