# FFmpeg 获取、版本与许可证（Milestone 5）

## 来源与版本（本次实际使用）

- **来源**：BtbN FFmpeg-Builds（官方 GitHub Releases）
  `https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip`
- **版本**：`ffmpeg version N-125994-gf944afd040-20260808`（master 构建，2026-08-08）
- **压缩包大小**：约 162.4 MB；解压后仅保留 `ffmpeg.exe`（≈138.7 MB）与 `ffprobe.exe`（≈138.5 MB）到
  项目内 `tools/ffmpeg/`（**不修改系统 PATH，不提交到 Git**）。
- **SHA-256**
  - zip：`CAA26D8F6BDC283E52DC762D1CEF21585A65691052D8C2D86EDFB874D529BC4F`
  - ffmpeg.exe：`3B77AD88D8002B26972D4511EAD09C4C51F59E12478A544779C81FBF391B0115`
  - ffprobe.exe：`FC71A19C3BC28CCE487D617B6BC4EB7B3DBCDD77F07A4F394C6E303AB53B5509`

## 许可证影响（重要）

该构建的 `configuration` 含 `--enable-gpl --enable-version3 --enable-libx264 --enable-libx265 …`，
因此这份 FFmpeg 二进制为 **GPL v3**（因链接 x264/x265 等 GPL 组件）。

- 使用该二进制进行录制无问题；但**分发**该二进制或包含它的安装包时，需遵守 GPL v3（提供对应源码等义务）。
- 若需避免 GPL：可改用 **LGPL** 构建，但通常不含 `libx264`；本项目的软件回退依赖 `libx264`，因此选择了 GPL 构建。
  如需 LGPL 方案，可改为仅用 `h264_qsv`（需 Intel QSV 可用）或改用其它 H.264 编码器，属需确认的方案变更。

## 定位顺序（运行时）

`Recording:FFmpegPath`（appsettings 配置） → 从应用运行目录**向上查找** `tools/ffmpeg/ffmpeg.exe` → PATH。
`ffprobe.exe` 优先取 `ffmpeg.exe` 同目录。开发时（在仓库内运行）向上查找会命中仓库的 `tools/ffmpeg`；
**部署时**请将 `tools/ffmpeg` 随应用一同发布，或用 `Recording:FFmpegPath` 指定绝对路径。

## 编码器能力探测

启动录制前对定位到的 FFmpeg 做**真实能力探测**：读取 `-version`，并尝试一次 0.2 秒的 `h264_qsv` 实际编码
（`-f lavfi -i color=... -c:v h264_qsv -f null -`）。成功→用 `h264_qsv`；失败→脱敏记录原因并回退 `libx264`。
本机实测：QuickSync 探测 **False**（无可用 Intel QSV）→ 使用 `libx264`。

## 磁盘预算

`tools/ffmpeg`（ffmpeg+ffprobe）≈ **277 MB**，计入 FFmpeg 预算（PROJECT.md 14.1 ≤ 0.3 GB 目标，略高，
如需压缩可只保留 `ffmpeg.exe` 或选用更精简构建）。会议 MP4 属用户数据，不计入应用静态占用。
