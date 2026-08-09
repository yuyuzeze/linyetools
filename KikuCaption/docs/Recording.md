# 录屏设计（Milestone 5）

> `KikuCaption.Recording`，依赖 `Core`，不依赖 WPF。以结构化参数管理 FFmpeg 子进程录制屏幕/窗口 +
> 系统声音为 MP4。FFmpeg 获取/版本/许可证见 [FFmpeg.md](FFmpeg.md)。

## 桌面与窗口捕获

- **整屏**：`-f gdigrab -framerate 15 -i desktop`。
- **指定窗口**：`-f gdigrab -framerate 15 -i title=<窗口标题>`。窗口通过 user32 `EnumWindows` 枚举
  （可见、有标题、排除 shell 窗口），标题在 UI 中展示；开始前用 `WindowEnumerator.WindowExists` 复核目标存在。
- **参数全部经 `ProcessStartInfo.ArgumentList`**（`title=...`、输出路径均为单个 token），**绝不经 cmd/PowerShell 字符串拼接**，含空格/特殊字符也不会命令注入。

### gdigrab 已知限制（如实记录）

- 最小化窗口无法正确捕获；硬件加速/DWM 合成窗口可能录成黑屏；
- 多个同名窗口无法区分（按标题匹配第一个）；标题变化会导致匹配失效；
- DPI 缩放/多显示器下坐标与分辨率可能与预期不同（实测桌面被记录为 2216×1278 即缩放后尺寸）。
- 因此“指定窗口”并非对所有窗口都可靠；普通顶层应用窗口通常可用。自动化测试对枚举窗口逐个尝试，取第一个可成功产出可播放 MP4 的窗口。

## 视频编码

默认 **15 FPS、H.264**；优先 `h264_qsv`（经真实短编码探测），失败回退 `libx264 -preset veryfast -crf 23`。
`-pix_fmt yuv420p -movflags +faststart`。UI 显示实际使用的编码器。

## 系统声音与音画复用

- 录制音频来自 NAudio **WASAPI Loopback**（M1），格式 16 kHz/mono/int16（§M5 允许直接复用，最小改动）。
  录制分支使用**独立的第二个 loopback 捕获**，与识别分支**背压隔离**（两者取自同一系统输出，音频一致）。
- PCM 经**每次录制唯一、当前用户 ACL** 的命名管道送入 FFmpeg：`-f s16le -ar 16000 -ac 1 -i \\.\pipe\<guid>`，
  输出 AAC（16 kHz/mono）。
- 写入 raw PCM 不加 `-use_wallclock_as_timestamps`（否则突发写入会把音频时间戳挤到 ~0），由采样率推导时间戳。

## 连续音频时间轴（M5 修正核心）

旧逻辑"仅在有 PCM 时才写管道"会丢失启动/静音间隔，导致音频比视频短、内容错位。现改为**以单调时钟驱动的
连续音频时间轴**（[`AudioTimeline.cs`](../src/KikuCaption.Recording/Muxing/AudioTimeline.cs)，纯逻辑、可单测）：

- **单调时钟**：FFmpeg 启动即 `Stopwatch.StartNew()` 为录制纪元（epoch），`Reset()` 丢弃 warm-up 积压。
- **期望样本数**：`expectedSamples = floor(elapsed × 16000)`；输出循环每 20 ms 帧（320 样本/640 字节）产出
  `due = expected − written` 的整帧——**有 WASAPI 数据写真实 PCM，无数据写零值静音**，音频时间轴从不暂停。
- **调度漂移校正**：产出量由时钟决定而非累加，因此**无累计漂移**（2 min→1,920,000、30 min→28,800,000 样本，单测验证）。
- **有界 jitter buffer**：默认 1 s（32,000 字节），溢出丢最旧样本并计数（`DroppedLateSamples`）；WASAPI/UI 线程
  的 `AppendRealPcm` 非阻塞（**不阻塞捕获、内存不无界**）。
- **停止补齐/裁剪**：`Flush(recordingEnd)` 补齐或裁剪到明确会话结束时间（`target = floor(end × 16000)`），绝不过写。
- 指标经 `AudioMetrics` 暴露：`WrittenSamples/ExpectedSamples/InsertedSilenceSamples/DroppedLateSamples/ClockErrorMs`。

## 时间基准与同步（实测状态）

- 单一会话由 `MeetingSession`（M4）确定；字幕 `StartTime/EndTime`、MP4 时间轴以录制开始为基准。
- **内容同步（唇同步关键指标）已达标 ≤500 ms**：objective beep-marker 测试实测间隔 4.97/10.05/2.72 s（期望 5/10/3），
  全程累计漂移 **−257 ms ≤ 500 ms**，开头静音不缩短、中间静音不消失。见 [Verification.md](Verification.md)。
- **音视频总时长差未达 ≤500 ms 门槛（实测 ~1.9 s，已诚实记录、未隐藏）**：根因是 **FFmpeg 从命名管道读取音频
  慢于实时**，`q` 停止时管道缓冲尾部被丢弃，形成**恒定（非漂移）尾部亏空**（`vStart = aStart = 0`，非起始偏移）。
  这是"边写边复用"活流管道路径的固有限制；**未使用硬编码 `-itsoffset` 掩盖**。
- PROJECT.md §5.3 允许的"临时 WAV + 停止时 `-c copy` 无损复用"路径可让总时长差也 ≤500 ms；经用户确认后
  **决定保留当前管道方案**，不切换。故 M5 sync 在"总时长差"一项判定为**未接受/部分通过**。

## 正常停止流程

1. 捕获 `endElapsed = _clock.Elapsed`（会话结束时刻），取消 WASAPI pump 与输出循环并等待其结束；
2. `Flush(endElapsed)` 将时间轴补齐/裁剪到会话结束时刻并写入管道尾部，再关闭管道（音频 EOF），`Task.Delay(300)` 让 FFmpeg drain；
3. 向 FFmpeg `stdin` 写入 `q` 优雅停止；
4. 等待退出（≤15s），超时才 `Kill(entireProcessTree)`；读取退出码；`ffprobe` 校验容器/流/可播放性；
5. 组装 `RecordingResult`（**仅当退出正常且 ffprobe 可播放才 IsComplete=true**）；
6. 更新 M4 会话的 `RecordingPath` 与 `session.json`。

## 异常退出与文件保留

- FFmpeg 启动失败/运行中崩溃/目标窗口关闭/管道失败：**保留已生成文件、不删除**，标记 `IsComplete=false` 并给出信息，
  **绝不把 0 字节或不可播放文件当成功**。
- Windows **Job Object（kill-on-close）** + 显式 kill-tree：应用退出/崩溃不留孤儿 FFmpeg。
- 未采用 fragmented MP4/临时容器 remux（保持 `+faststart` 标准 MP4）；如需此类恢复策略属需确认的方案变更。

## 与会话/字幕的协调

- 录屏 MP4 写入**当前会话目录** `Meetings/<ts>_<id>/meeting.mp4`，与字幕同一 Session ID。
- **录屏失败不影响字幕**（异常被捕获，字幕继续实时保存）；字幕失败时录屏继续（录制是更重要的产物），UI 状态明确。
- 开始按钮为“开始录制和字幕”，停止协调两条管线并保存最终状态。UI 阻止重复开始、运行中改目标、未选窗口就开始、FFmpeg 缺失时静默失败。

## 已知限制

- 见上文 gdigrab 限制；录制音频为 16 kHz（低保真，V1 允许）；QSV 需 Intel 硬件，无则 libx264（CPU 更高）。
- 部署时需随附 `tools/ffmpeg` 或配置 `Recording:FFmpegPath`。
