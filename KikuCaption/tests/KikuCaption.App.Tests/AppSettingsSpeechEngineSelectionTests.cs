using System.IO;
using KikuCaption.App.Services;
using KikuCaption.Core.Enums;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Speech.SpeechCpp;
using Xunit;

namespace KikuCaption.App.Tests;

public sealed class AppSettingsSpeechEngineSelectionTests : IDisposable
{
    private sealed class Env : ISpeechCppEnvironment
    {
        public bool IsAvailable { get; init; }
    }

    private readonly string _dir;

    public AppSettingsSpeechEngineSelectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kiku_enginesel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void SpeechCpp_Selected_AndAvailable_ReportsSpeechCpp()
    {
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "SpeechCpp" }, new Env { IsAvailable = true });

        Assert.Equal(SpeechEngine.SpeechCpp, sel.Engine);
    }

    [Fact]
    public void SpeechCpp_Selected_ButUnavailable_FallsBackToWhisper()
    {
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "SpeechCpp" }, new Env { IsAvailable = false });

        Assert.Equal(SpeechEngine.Whisper, sel.Engine);
    }

    [Fact]
    public void Whisper_Selected_ReportsWhisper()
    {
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "Whisper" }, new Env { IsAvailable = true });

        Assert.Equal(SpeechEngine.Whisper, sel.Engine);
    }

    [Fact]
    public void Model_PrefersBundledPath_OverName()
    {
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "SpeechCpp", SpeechCppModel = "haas-ja-v1.0" },
            new Env { IsAvailable = true },
            bundledModelPath: @"C:\app\models\speech\Haas.gguf");

        Assert.Equal(@"C:\app\models\speech\Haas.gguf", sel.SpeechCppModel);
    }

    [Fact]
    public void Model_ExplicitExistingPath_Wins()
    {
        var real = Path.Combine(_dir, "custom.gguf");
        File.WriteAllText(real, "stub");
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "SpeechCpp", SpeechCppModel = real },
            new Env { IsAvailable = true },
            bundledModelPath: @"C:\app\models\speech\Haas.gguf");

        Assert.Equal(real, sel.SpeechCppModel);
    }

    [Fact]
    public void Model_NoBundle_FallsBackToName()
    {
        var sel = new AppSettingsSpeechEngineSelection(
            new SpeechSettings { Engine = "SpeechCpp", SpeechCppModel = "haas-ja-v1.0" },
            new Env { IsAvailable = true });

        Assert.Equal("haas-ja-v1.0", sel.SpeechCppModel);
    }
}
