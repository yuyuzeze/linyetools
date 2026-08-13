using System.IO;
using KikuCaption.App.Services;
using KikuCaption.Core.Models;
using Xunit;

namespace KikuCaption.App.Tests;

public sealed class CorrectionModelLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiku-medium", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingModel_IsUnavailable()
    {
        var result = Locator().Check();
        Assert.False(result.IsAvailable);
        Assert.Equal(_root, result.CacheRoot);
    }

    [Fact]
    public void ManualCompleteDirectory_IsAvailable()
    {
        var dir = Path.Combine(_root, "faster-whisper-medium");
        Directory.CreateDirectory(dir);
        foreach (var file in new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" })
            File.WriteAllText(Path.Combine(dir, file), "x");
        var result = Locator().Check();
        Assert.True(result.IsAvailable);
        Assert.Equal(dir, result.ModelPath);
    }

    [Fact]
    public void MissingRequiredFile_IsUnavailable()
    {
        var dir = Path.Combine(_root, "faster-whisper-medium");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.bin"), "x");
        Assert.False(Locator().Check().IsAvailable);
    }

    [Fact]
    public void SiblingManualDownload_IsAvailable()
    {
        var cache = Path.Combine(_root, "models", "whisper");
        var model = Path.Combine(_root, "models", "faster-whisper-medium");
        Directory.CreateDirectory(cache);
        CreateCompleteModel(model);

        var result = Locator(cache).Check();

        Assert.True(result.IsAvailable);
        Assert.Equal(Path.GetFullPath(model), result.ModelPath);
    }

    private CorrectionModelLocator Locator(string? cacheRoot = null) => new(
        new SpeechOptionsProvider(new SpeechOptions { Language = "ja", ModelCacheDirectory = cacheRoot ?? _root }), 1);

    private static void CreateCompleteModel(string model)
    {
        Directory.CreateDirectory(model);
        foreach (var file in new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" })
            File.WriteAllText(Path.Combine(model, file), "x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
