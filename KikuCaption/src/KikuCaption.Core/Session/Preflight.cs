namespace KikuCaption.Core.Session;

/// <summary>Severity of a single pre-start check (Milestone 7 §2).</summary>
public enum PreflightSeverity
{
    /// <summary>OK.</summary>
    Pass,

    /// <summary>Degraded but the session may still start (e.g. translation or recording unavailable).</summary>
    Warn,

    /// <summary>Must be resolved before a real-time caption session can start.</summary>
    Block
}

/// <summary>One pre-start check result.</summary>
public sealed record PreflightCheck(string Name, PreflightSeverity Severity, string Detail);

/// <summary>Facts gathered by the App before evaluating readiness. All are simple, testable values.</summary>
public sealed record PreflightInputs
{
    // Blocking prerequisites for any real-time caption session.
    public bool DotNetOk { get; init; } = true;
    public bool PythonOk { get; init; }
    public bool WhisperDepsOk { get; init; }
    public bool ModelOk { get; init; }
    public bool SqliteOk { get; init; }
    public bool WasapiDeviceOk { get; init; }
    public bool OutputWritable { get; init; }
    public bool DiskOk { get; init; }
    public double FreeDiskGb { get; init; }
    public double RequiredDiskGb { get; init; }

    // Recording-related — never silently degraded; surfaced for an explicit user choice.
    public bool FfmpegOk { get; init; }
    public bool FfprobeOk { get; init; }
    public bool EncoderOk { get; init; }
    public bool CaptureTargetOk { get; init; }

    // Translation — only relevant when enabled; unavailability is a warning (original still saved).
    public bool TranslationEnabled { get; init; }
    public bool TranslationConfigOk { get; init; }
    public bool DpapiKeyReadable { get; init; }
}

/// <summary>The full readiness report with blocking/recording/translation summary.</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightCheck> Checks)
{
    public bool HasBlocking => Checks.Any(c => c.Severity == PreflightSeverity.Block);
    public bool HasWarnings => Checks.Any(c => c.Severity == PreflightSeverity.Warn);

    /// <summary>True when screen/window recording can run; false means the user must choose.</summary>
    public bool RecordingAvailable { get; init; } = true;

    /// <summary>True when JA→ZH translation is enabled and configured; false = original-only.</summary>
    public bool TranslationAvailable { get; init; }
}

/// <summary>
/// Pure evaluator turning gathered facts into a readiness report (Milestone 7 §2). Audio, model,
/// storage, output-writable and disk are blocking; recording is a non-silent warning that requires
/// an explicit choice; translation is a warning that allows original-only.
/// </summary>
public static class PreflightEvaluator
{
    public static PreflightReport Evaluate(PreflightInputs i)
    {
        var checks = new List<PreflightCheck>
        {
            Req(".NET 运行环境", i.DotNetOk, "缺少 .NET 运行环境"),
            Req("Python 运行环境", i.PythonOk, "未找到可用的 Python"),
            Req("faster-whisper 依赖", i.WhisperDepsOk, "缺少识别依赖"),
            Req("Whisper small 模型", i.ModelOk, "模型缺失或无法加载"),
            Req("SQLite 可打开", i.SqliteOk, "数据库无法打开"),
            Req("系统音频输出设备", i.WasapiDeviceOk, "未检测到 WASAPI 输出设备"),
            Req("输出目录可写", i.OutputWritable, "输出目录不可写（请选择用户可写目录）"),
            Req("磁盘空间充足", i.DiskOk, $"可用 {i.FreeDiskGb:0.0} GB < 需 {i.RequiredDiskGb:0.0} GB"),
        };

        bool recordingAvailable = i.FfmpegOk && i.FfprobeOk && i.EncoderOk && i.CaptureTargetOk;
        // Recording problems are warnings (explicit choice), never blocking or silent.
        checks.Add(Warnable("FFmpeg / ffprobe", i.FfmpegOk && i.FfprobeOk, "未找到 FFmpeg/ffprobe（可仅字幕，不录屏）"));
        checks.Add(Warnable("视频编码器探测", i.EncoderOk, "编码器不可用（可仅字幕）"));
        checks.Add(Warnable("捕获目标有效", i.CaptureTargetOk, "所选捕获目标无效（请重新选择）"));

        bool translationAvailable = false;
        if (i.TranslationEnabled)
        {
            bool ok = i.TranslationConfigOk && i.DpapiKeyReadable;
            translationAvailable = ok;
            checks.Add(Warnable("翻译配置 + 密钥", ok, "翻译配置或密钥不可用（将只保存原文）"));
        }

        return new PreflightReport(checks)
        {
            RecordingAvailable = recordingAvailable,
            TranslationAvailable = translationAvailable
        };
    }

    private static PreflightCheck Req(string name, bool ok, string failDetail)
        => new(name, ok ? PreflightSeverity.Pass : PreflightSeverity.Block, ok ? "通过" : failDetail);

    private static PreflightCheck Warnable(string name, bool ok, string failDetail)
        => new(name, ok ? PreflightSeverity.Pass : PreflightSeverity.Warn, ok ? "通过" : failDetail);
}
