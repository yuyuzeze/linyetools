# KikuCaption

KikuCaption 是一款面向 Windows 11 的本地会议录制与实时字幕工具，主要用于 Microsoft Teams、线上培训、技术说明会和本地媒体回放。

它可以同时录制屏幕、系统声音和麦克风，在本机使用 faster-whisper 生成日语或中文字幕，并可通过公司提供的 OpenAI 兼容 API 翻译字幕、整理会议要点。

## 主要功能

- 录制整个屏幕或指定窗口，保存为 MP4。
- 同时采集系统声音和麦克风；自己的发言也可进入录音与实时字幕。
- 使用本地 faster-whisper 进行日语、中文实时识别，音频不会发送到语音识别云服务。
- 显示渐进式字幕、置顶字幕浮窗和从第一条到最后一条的完整会议时间线。
- 将最终字幕实时保存到 SQLite，并导出 JSON、TXT 和 SRT；程序异常退出后可恢复。
- 可选调用公司 OpenAI 兼容 API，将最终字幕翻译为中文、日文或英文。
- 支持专业术语词典，为不同识别语言保存和切换 Initial Prompt 与 Hotwords。
- 会议结束后可使用本地 `medium/int8` 模型生成校正版字幕。
- 可根据最终原文字幕生成会议要点 Markdown；不会发送录音或视频。
- 内置会议回放，点击字幕可跳转到对应时间，并可调整字幕时间偏移。
- 支持字幕搜索、历史会议浏览及删除整个会议记录。
- 最小化到系统托盘，可从托盘开始/停止会议、显示浮窗或打开设置。
- 界面支持简体中文、English 和日本語。

## 运行环境

- Windows 11 x64
- 建议 16 GB 内存
- 64 位 Python 3.12 或 3.13
- 无独立显卡也可以运行；默认使用 CPU `int8`
- 实时模型：faster-whisper `small`
- 可选校正模型：faster-whisper `medium`

模型加载后，Python Worker 通常会占用数百 MB 内存。启用“后台预热”可以缩短第一次开始识别的等待时间，但会提前占用约 350–600 MB；取消设置或退出程序时会释放。

## 安装发布版

1. 解压 `KikuCaption-<版本>-win-x64.zip` 到桌面、文档等当前用户可写目录。
2. 安装 64 位 Python 3.12 或 3.13，并确保可以在 PowerShell 中执行 `python --version`。
3. 在解压后的 KikuCaption 根目录打开 PowerShell，执行一次：

   ```powershell
   .\setup-python.ps1
   ```

4. 脚本将在发布目录中创建：

   ```text
   python\whisper_worker\.venv
   ```

   并安装 faster-whisper、CTranslate2 等锁定依赖。
5. 启动 `KikuCaption.exe`，等待自动环境检查完成。

如果 PowerShell 阻止本地脚本，可仅对本次进程执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\setup-python.ps1
```

`.NET` 已包含在自包含发布版内，不需要另外安装 SDK。FFmpeg 随正式发布包放在 `tools\ffmpeg`；若使用自行构建的包，则需要在设置中指定 FFmpeg。

## 模型目录

默认模型缓存根目录为：

```text
models\whisper
```

实时识别需要可用的 `small` CTranslate2/faster-whisper 模型。启用“停止后自动校正”前，还需要完整的 `medium` CTranslate2 模型；程序会在设置中检查模型是否存在，模型缺失时不会启用自动校正。

仅放入模型文件不能替代 Python Worker 环境；环境检查中“Whisper 模型”和“faster-whisper Worker”是两个独立项目。

## 第一次使用

1. 打开右上角的环境状态，确认以下关键项目正常：
   - faster-whisper Worker
   - Whisper 模型
   - 音频输出设备
   - FFmpeg / FFprobe
   - 输出目录和可用磁盘空间
2. 在“设置 → 常用”选择界面语言、默认识别语言和会议输出目录。
3. 在“设置 → 字幕”调整浮窗字号、字体、透明度和行数。
4. 如需提高专业术语命中率，在“词典”复制内置词典，加入业务术语并设为当前词典。
5. 如需翻译或会议要点，在“设置 → 翻译”填写公司 API 的 Endpoint、Model、认证方式并安全保存 API Key，然后测试连接。

## 录制会议

1. 在首页选择识别语言。
2. 按需启用翻译。来源语言跟随识别语言，目标语言在设置中指定。
3. 点击“开始会议”。
4. 选择录制整个屏幕或指定窗口，并选择是否采集系统声音和麦克风。
5. 开始后，右侧显示完整会议字幕；字幕浮窗可以独立显示、置顶或鼠标穿透。
6. 点击“停止”安全结束录屏、完成字幕写入并导出文件。

会议运行时可最小化到任务栏通知区域，识别、录制、翻译和字幕保存不会停止。

## 历史、搜索与回放

- 首页左侧显示历史会议，选择后可加载其完整字幕。
- 点击“回放”在应用内打开 MP4；点击字幕可跳到对应时间。
- 使用“搜索”查找原文或译文，并跳转到相应字幕和回放位置。
- 若字幕与视频存在固定偏移，可在回放窗口调整字幕时间偏移。
- 删除会议前会显示确认框；确认后会删除数据库记录以及该会议的整个输出文件夹，此操作不可撤销。

## 校正版字幕

实时字幕优先保证及时显示。对于语速较快或连续讲话的会议，可在停止后使用 `medium/int8` 对完整录音重新识别，生成校正版字幕。

- 可在设置中选择是否停止后自动校正。
- 校正运行在本机，耗时高于实时识别。
- 校正失败不会删除实时字幕或录屏。
- 使用前请确认 `medium` 模型已经完整下载到程序配置的模型缓存目录。

## 翻译与会议要点

翻译功能只发送最终确认的原文字幕，不发送音频、视频或 partial 字幕。取消首页的翻译勾选后，后续字幕将停止创建新的翻译任务。

会议停止后或打开历史会议时，可以生成会议要点：

- 单人讲解：整理内容概要、主要主题、关键知识点、流程、结论和注意事项。
- 多人讨论：整理会议概述、讨论主题、主要观点、决定事项、待办事项、未解决问题和风险。
- 当前版本不进行说话人识别或角色推断。
- 输入仅使用最终原文字幕，不包含翻译文本、录音或视频。
- 结果以 `meeting-summary.md` 保存到对应会议目录。

API Key 使用 Windows DPAPI 按当前用户加密保存，不写入 `appsettings.json`、SQLite、会议文件或日志。

## 会议输出

每场会议使用独立目录，通常包含：

```text
Meetings\<日期时间_会话ID>\
├─ meeting.mp4
├─ transcript.json
├─ transcript.txt
├─ transcript.srt
├─ translation.srt
├─ corrected-transcript.json / .txt / .srt   # 启用校正且成功时
├─ meeting-summary.md                         # 生成会议要点后
└─ session.json
```

数据库默认位于输出根目录的 `kikucaption.db`。会议文件属于用户数据，不会因为卸载应用而自动删除。

## 常见问题

### 环境检查提示 faster-whisper Worker 缺失

模型存在并不代表 Worker 环境存在。请在发布包根目录执行：

```powershell
.\setup-python.ps1
```

执行完成后重新启动程序并再次检查环境。

### 首次开始识别较慢

首次需要启动 Python Worker 并加载模型。可在“设置 → 常用”启用后台预热，以内存换取更快的首次开始速度。

### 校正版字幕生成失败

确认 `medium` 模型完整存在。若日志包含证书或联网错误，通常是模型路径没有被正确识别，程序尝试访问远程模型仓库；请检查配置的模型缓存路径及模型文件结构。

### 翻译或会议要点失败

先在翻译设置中执行“测试连接”。检查 Endpoint、Model/Deployment、API Version、认证模式和 API Key。HTTP 200 但仍失败通常代表公司 API 返回内容不符合预期 JSON 格式。

### 录屏不可用但字幕正常

检查 FFmpeg、FFprobe 和录制目标。录屏模块故障会与字幕隔离，原文字幕仍会继续保存。

## 隐私说明

- faster-whisper 识别和专业术语词典均在本机运行。
- 翻译和会议要点只向配置的公司 API 发送必要的最终文字。
- 普通日志不记录完整字幕、翻译正文、原始 PCM 或 API Key。
- 请遵守所在公司的会议录制、转录和数据处理政策。

## 从源码构建

开发环境需要 .NET 10 SDK、Windows 11 和 Python 3.12/3.13：

```powershell
dotnet restore KikuCaption.sln
dotnet build KikuCaption.sln -c Debug
.\scripts\setup-python.ps1
dotnet run --project src\KikuCaption.App\KikuCaption.App.csproj -c Debug
```

运行测试：

```powershell
dotnet test KikuCaption.sln
.\python\whisper_worker\.venv\Scripts\python.exe -m pytest -q python\whisper_worker\tests
```

生成发布包：

```powershell
.\scripts\publish.ps1
```

发布目录和 ZIP 会包含根目录 `setup-python.ps1`，但不会包含 Python `.venv`、模型、会议数据、日志或密钥。

## 文档

- [`docs/UserGuide.md`](docs/UserGuide.md)：详细用户操作说明
- [`docs/Delivery.md`](docs/Delivery.md)：发布与交付说明
- [`docs/Architecture.md`](docs/Architecture.md)：技术架构
- [`docs/Protocol.md`](docs/Protocol.md)：C# 与 Python Worker 协议
- [`docs/Verification.md`](docs/Verification.md)：测试和人工验证记录
- [`docs/README.development-history.md`](docs/README.development-history.md)：旧版开发记录备份
- [`PROJECT.md`](PROJECT.md)：项目启动阶段的原始需求和里程碑基线，并非当前成品功能清单

## 许可证与第三方组件

第三方依赖及分发义务见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。随包 FFmpeg 为 GPL 构建；对外分发时请遵守相应许可证要求。

KikuCaption — Designed & built by Yu. Contributor: Claude.
