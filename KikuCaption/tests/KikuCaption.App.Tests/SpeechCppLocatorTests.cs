using System.IO;
using KikuCaption.App.Services;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// Deterministic tests for the bundled-tools walk-up (no dependence on any installed speech.cpp).
/// Builds a temporary <c>tools/speech</c> tree and checks discovery from a nested base directory.
/// </summary>
public sealed class SpeechCppLocatorTests : IDisposable
{
    private readonly string _root;

    public SpeechCppLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kiku_speechcpp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Nested()
    {
        // Mimics an app bin dir several levels below the repo root that holds tools/speech.
        var nested = Path.Combine(_root, "src", "App", "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(nested);
        return nested;
    }

    [Fact]
    public void LocateExecutable_FindsBundledExe_ByWalkingUp()
    {
        var toolsSpeech = Path.Combine(_root, "tools", "speech");
        Directory.CreateDirectory(toolsSpeech);
        var exe = Path.Combine(toolsSpeech, "speech.exe");
        File.WriteAllText(exe, "stub");

        var located = SpeechCppLocator.LocateExecutable(null, Nested());

        Assert.Equal(exe, located);
    }

    [Fact]
    public void LocateExecutable_ExplicitPath_Wins()
    {
        var explicitExe = Path.Combine(_root, "custom-speech.exe");
        File.WriteAllText(explicitExe, "stub");

        var located = SpeechCppLocator.LocateExecutable(explicitExe, Nested());

        Assert.Equal(explicitExe, located);
    }

    [Fact]
    public void LocateBundledModel_SingleGguf_Found()
    {
        var modelsDir = Path.Combine(_root, "models", "speech");
        Directory.CreateDirectory(modelsDir);
        var gguf = Path.Combine(modelsDir, "Haas-616M-ja-v1.0-Q8_0.gguf");
        File.WriteAllText(gguf, "stub");

        var located = SpeechCppLocator.LocateBundledModel(Nested(), "haas");

        Assert.Equal(gguf, located);
    }

    [Fact]
    public void LocateBundledModel_None_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models", "speech"));

        var located = SpeechCppLocator.LocateBundledModel(Nested(), "haas");

        Assert.Null(located);
    }

    [Fact]
    public void LocateBundledModel_Multiple_MatchesNameHint()
    {
        var modelsDir = Path.Combine(_root, "models", "speech");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, "Haas-616M-ja-v1.0-Q8_0.gguf"), "stub");
        File.WriteAllText(Path.Combine(modelsDir, "reazonspeech-nemo-619M-v2-Q8_0.gguf"), "stub");

        var located = SpeechCppLocator.LocateBundledModel(Nested(), "haas");

        Assert.NotNull(located);
        Assert.Contains("Haas", Path.GetFileName(located!));
    }
}
