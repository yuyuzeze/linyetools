using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KikuCaption.App.Localization;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// UI-R3.1: environment probes emit language-neutral message codes that the App localizes; internal
/// enum values are shown via localized display; fallback behaviour is unified.
/// </summary>
public class LocalizedDiagnosticsTests
{
    // Every message/remediation code a probe can emit — must resolve in all three languages.
    private static readonly string[] ProbeCodes =
    {
        "EnvMsg.DotNet.Ok", "EnvMsg.DotNet.Old", "EnvRem.DotNet.Old",
        "EnvMsg.Python.Ok", "EnvMsg.Python.OkOldish", "EnvMsg.Python.Missing", "EnvRem.Python.Missing",
        "EnvMsg.FFmpeg.Missing", "EnvMsg.FFmpeg.NotRunnable", "EnvMsg.FFmpeg.Ok",
        "EnvRem.FFmpeg.Missing", "EnvRem.FFmpeg.NotRunnable",
        "EnvMsg.FFprobe.MissingBeside", "EnvMsg.FFprobe.MissingPair", "EnvMsg.FFprobe.NotRunnable", "EnvMsg.FFprobe.Ok",
        "EnvRem.FFprobe.Missing", "EnvRem.FFprobe.NotRunnable",
        "EnvMsg.Disk.Info", "EnvMsg.Disk.Error", "EnvRem.Disk.Low", "EnvRem.Disk.Error",
        "EnvMsg.Worker.NoScript", "EnvMsg.Worker.NoPython", "EnvMsg.Worker.Ok",
        "EnvRem.Worker.NoScript", "EnvRem.Worker.NoPython",
        "EnvMsg.Model.Ok", "EnvMsg.Model.Missing", "EnvRem.Model.Missing",
        "EnvMsg.Audio.EnumFail", "EnvMsg.Audio.Missing", "EnvMsg.Audio.Ok",
        "EnvRem.Audio.EnumFail", "EnvRem.Audio.Missing",
        "EnvMsg.Output.Ok", "EnvMsg.Output.NotWritable", "EnvRem.Output.NotWritable",
        "EnvMsg.Trans.Disabled", "EnvMsg.Trans.Ok", "EnvMsg.Trans.Incomplete", "EnvRem.Trans.Incomplete"
    };

    [Fact] // each diagnostic MessageCode has a resource in all three languages
    public void EveryProbeCode_ResolvesInThreeLanguages()
    {
        foreach (var culture in LocalizedStrings.SupportedCultures)
        {
            var table = LocalizedStrings.Tables[culture];
            foreach (var code in ProbeCodes)
            {
                Assert.True(table.ContainsKey(code), $"{culture} missing probe code {code}");
                Assert.False(string.IsNullOrWhiteSpace(table[code]), $"{culture} empty for {code}");
            }
        }
    }

    [Fact] // the App localizes the detail from the code; Version/ResolvedPath are kept verbatim
    public void ItemViewModel_LocalizesDetail_KeepsVersionAndPath()
    {
        var loc = new LocalizationService();
        var result = new DependencyCheckResult
        {
            Kind = DependencyKind.FFmpeg,
            Name = "FFmpeg",
            Status = EnvironmentCheckStatus.Ok,
            DetectedVersion = "FFmpeg 6.1.1",
            ResolvedPath = @"C:\tools\ffmpeg\ffmpeg.exe",
            MessageCode = "EnvMsg.FFmpeg.Ok"
        };
        var item = new EnvironmentItemViewModel(result, loc);

        Assert.Equal("已检测到可运行的 FFmpeg。", item.Detail);
        loc.SetLanguage(LocalizedStrings.EnUS);
        Assert.Equal("A runnable FFmpeg was detected.", item.Detail);
        loc.SetLanguage(LocalizedStrings.JaJP);
        Assert.Equal("実行可能な FFmpeg を検出しました。", item.Detail);

        // Version and path are never translated or altered.
        Assert.Equal("FFmpeg 6.1.1", item.DetectedVersion);
        Assert.Equal(@"C:\tools\ffmpeg\ffmpeg.exe", item.ResolvedPath);
    }

    [Fact] // message arguments (raw values) are inserted verbatim
    public void ItemViewModel_FormatsArguments()
    {
        var loc = new LocalizationService();
        var result = new DependencyCheckResult
        {
            Kind = DependencyKind.DiskSpace,
            Name = "disk",
            Status = EnvironmentCheckStatus.Ok,
            MessageCode = "EnvMsg.Disk.Info",
            MessageArguments = new[] { "C:\\", "123.4", "2.0" }
        };
        var item = new EnvironmentItemViewModel(result, loc);

        Assert.Contains("C:\\", item.Detail);
        Assert.Contains("123.4", item.Detail);
        Assert.Contains("2.0", item.Detail);
    }

    private sealed class CountingChecker : IEnvironmentChecker
    {
        private readonly EnvironmentReport _report;
        public int Calls { get; private set; }
        public CountingChecker(EnvironmentReport report) => _report = report;
        public Task<EnvironmentReport> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_report);
        }
    }

    [Fact] // switching language re-localizes the existing results without re-running the probes
    public void EnvironmentResults_ReLocalize_WithoutReProbing()
    {
        var loc = new LocalizationService();
        var report = new EnvironmentReport(new[]
        {
            new DependencyCheckResult
            {
                Kind = DependencyKind.FFmpeg, Name = "FFmpeg",
                Status = EnvironmentCheckStatus.Ok, MessageCode = "EnvMsg.FFmpeg.Ok"
            }
        });
        var checker = new CountingChecker(report);
        var vm = new EnvironmentPageViewModel(checker, loc, NullLogger<EnvironmentPageViewModel>.Instance);

        vm.CheckCommand.Execute(null);
        Assert.Equal(1, checker.Calls);
        var zh = vm.Items[0].Detail;

        loc.SetLanguage(LocalizedStrings.JaJP);

        Assert.Equal(1, checker.Calls);                 // no re-probe
        Assert.NotEqual(zh, vm.Items[0].Detail);        // but the text is re-localized
        Assert.Equal("実行可能な FFmpeg を検出しました。", vm.Items[0].Detail);
    }

    [Theory] // internal recording-target values map to localized display; value stays screen/window
    [InlineData(LocalizedStrings.ZhCN, "screen", "整个屏幕")]
    [InlineData(LocalizedStrings.EnUS, "screen", "Entire screen")]
    [InlineData(LocalizedStrings.JaJP, "screen", "画面全体")]
    [InlineData(LocalizedStrings.ZhCN, "window", "指定窗口")]
    [InlineData(LocalizedStrings.EnUS, "window", "Specific window")]
    [InlineData(LocalizedStrings.JaJP, "window", "指定したウィンドウ")]
    public void CaptureTarget_DisplayIsLocalized(string culture, string value, string expected)
    {
        var loc = new LocalizationService();
        loc.SetLanguage(culture);
        Assert.Equal(expected, loc["Capture." + value]);
    }

    [Fact] // SetLanguage returns false for an unknown/unchanged culture and keeps the current one
    public void SetLanguage_ReturnsFalse_ForUnknownOrSame_AndKeepsCurrent()
    {
        var loc = new LocalizationService();
        Assert.True(loc.SetLanguage(LocalizedStrings.JaJP));   // valid change
        Assert.False(loc.SetLanguage(LocalizedStrings.JaJP));  // already current
        Assert.False(loc.SetLanguage("ko-KR"));                // unknown
        Assert.Equal(LocalizedStrings.JaJP, loc.CurrentLanguage);
    }

    [Theory] // persisted/corrupt codes normalize to zh-CN; supported codes pass through
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("en-US", "en-US")]
    [InlineData("ko-KR", "zh-CN")]
    [InlineData("", "zh-CN")]
    [InlineData(null, "zh-CN")]
    public void NormalizeCulture_FallsBackToZhCn(string? persisted, string expected)
    {
        Assert.Equal(expected, LocalizationService.NormalizeCulture(persisted));
    }

    [Fact] // no known Chinese probe sentence remains hard-coded in the English/Japanese detail
    public void KnownChineseProbeSentences_AreNotShownInOtherLanguages()
    {
        string[] oldChinese =
        {
            "已在 .NET 10 或更高版本运行时上运行。",
            "已检测到可用的 Python 解释器",
            "已找到本地识别 Worker 脚本",
            "已检测到 Whisper 模型缓存",
            "已检测到默认音频输出设备",
            "已检测到可运行的 FFmpeg。"
        };

        foreach (var culture in new[] { LocalizedStrings.EnUS, LocalizedStrings.JaJP })
        {
            var table = LocalizedStrings.Tables[culture];
            foreach (var value in table.Values)
            {
                foreach (var cn in oldChinese)
                {
                    Assert.DoesNotContain(cn, value);
                }
            }
        }
    }
}
