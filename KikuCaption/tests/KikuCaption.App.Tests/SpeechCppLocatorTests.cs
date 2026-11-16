using System.IO;
using KikuCaption.App.Services;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// Deterministic tests for the exe-relative resolution (no walk-up, no installed speech.cpp). Builds
/// a temporary base dir that holds <c>tools/speech</c> and <c>models/speech</c> and resolves from it.
/// </summary>
public sealed class SpeechCppLocatorTests : IDisposable
{
    private readonly string _baseDir;

    public SpeechCppLocatorTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "kiku_speechcpp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void LocateExecutable_FindsExe_RelativeToBaseDir()
    {
        var toolsSpeech = Path.Combine(_baseDir, "tools", "speech");
        Directory.CreateDirectory(toolsSpeech);
        var exe = Path.Combine(toolsSpeech, "speech.exe");
        File.WriteAllText(exe, "stub");

        Assert.Equal(exe, SpeechCppLocator.LocateExecutable(null, _baseDir));
    }

    [Fact]
    public void LocateExecutable_NotPresent_ReturnsNull()
    {
        Assert.Null(SpeechCppLocator.LocateExecutable(null, _baseDir));
    }

    [Fact]
    public void LocateExecutable_ExplicitPath_Wins()
    {
        var explicitExe = Path.Combine(_baseDir, "custom-speech.exe");
        File.WriteAllText(explicitExe, "stub");

        Assert.Equal(explicitExe, SpeechCppLocator.LocateExecutable(explicitExe, _baseDir));
    }

    [Fact]
    public void LocateBundledModel_SingleGguf_Found()
    {
        var modelsDir = Path.Combine(_baseDir, "models", "speech");
        Directory.CreateDirectory(modelsDir);
        var gguf = Path.Combine(modelsDir, "Haas-616M-ja-v1.0-Q8_0.gguf");
        File.WriteAllText(gguf, "stub");

        Assert.Equal(gguf, SpeechCppLocator.LocateBundledModel(_baseDir, "haas"));
    }

    [Fact]
    public void LocateBundledModel_None_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_baseDir, "models", "speech"));

        Assert.Null(SpeechCppLocator.LocateBundledModel(_baseDir, "haas"));
    }

    [Fact]
    public void LocateBundledModel_Multiple_MatchesNameHint()
    {
        var modelsDir = Path.Combine(_baseDir, "models", "speech");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, "Haas-616M-ja-v1.0-Q8_0.gguf"), "stub");
        File.WriteAllText(Path.Combine(modelsDir, "reazonspeech-nemo-619M-v2-Q8_0.gguf"), "stub");

        var located = SpeechCppLocator.LocateBundledModel(_baseDir, "haas");

        Assert.NotNull(located);
        Assert.Contains("Haas", Path.GetFileName(located!));
    }
}
