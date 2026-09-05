using System.Diagnostics;
using System.IO;
using KikuCaption.App.Services;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.Core.Models;
using KikuCaption.Speech.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KikuCaption.App.Tests;

/// <summary>
/// R7B.2 REAL model verification. These call the actual <see cref="PythonModelRuntimeLoader"/> against a
/// real faster-whisper model using the project venv, so they only run when a valid model + venv are
/// present AND the opt-in env var KIKU_REAL_MODEL_TESTS=1 is set (they self-skip otherwise so the normal
/// suite stays fast and portable). They never fabricate success: with no valid model they simply report
/// "not found" and pass without asserting a load.
/// </summary>
public sealed class RealModelVerificationTests
{
    private readonly ITestOutputHelper _out;
    public RealModelVerificationTests(ITestOutputHelper output) => _out = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("KIKU_REAL_MODEL_TESTS") == "1";

    [Fact]
    public async Task Real_Small_RuntimeLoads_ViaProjectVenv()
    {
        if (!Enabled) { _out.WriteLine("skipped: KIKU_REAL_MODEL_TESTS != 1"); return; }

        var venv = FindVenvPython();
        var modelDir = FindValidModel();
        if (venv is null || modelDir is null)
        {
            _out.WriteLine($"not found: venv={venv is not null}, model={modelDir is not null}; keeping RuntimeVerified UNVERIFIED.");
            return; // no fake success
        }

        _out.WriteLine($"venv=present, model path-category=managed-or-hf-cache");
        var loader = new PythonModelRuntimeLoader(Worker(venv), NullLogger<PythonModelRuntimeLoader>.Instance);
        var sw = Stopwatch.StartNew();
        var result = await loader.TryLoadAsync(modelDir, CancellationToken.None);
        sw.Stop();
        _out.WriteLine($"real load result={result} elapsed_ms={sw.ElapsedMilliseconds}");
        Assert.Equal(ModelRuntimeLoadResult.Loaded, result);
    }

    [Fact]
    public async Task Real_Small_ManagedPath_ResolvesAndLoads()
    {
        if (!Enabled) { _out.WriteLine("skipped: KIKU_REAL_MODEL_TESTS != 1"); return; }

        var venv = FindVenvPython();
        var modelDir = FindValidModel();
        if (venv is null || modelDir is null) { _out.WriteLine("not found; skipping managed real-load."); return; }

        // Copy the real model into a TEMP managed layout (never touch the user's original).
        var cacheRoot = Path.Combine(Path.GetTempPath(), "kiku_managed_real_" + Guid.NewGuid().ToString("N"));
        var managedDir = Path.Combine(cacheRoot, "faster-whisper-small");
        try
        {
            CopyDir(modelDir, managedDir);
            InstallReceiptStore.Write(managedDir, new InstallReceipt(
                InstallReceiptStore.CurrentSchemaVersion, "whisper-small", "test",
                new string('0', 64), DateTime.UtcNow.ToString("O"), ModelVerificationLevel.StructureVerified));

            var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance);
            var resolution = locator.Resolve("small", WhisperModelPurpose.Realtime);
            _out.WriteLine($"managed resolve: IsManaged={resolution.IsManagedInstall}");
            Assert.True(resolution.IsManagedInstall);
            Assert.Equal(managedDir, resolution.ModelName); // the ABSOLUTE managed dir handed to Python

            var loader = new PythonModelRuntimeLoader(Worker(venv), NullLogger<PythonModelRuntimeLoader>.Instance);
            var sw = Stopwatch.StartNew();
            var result = await loader.TryLoadAsync(resolution.ModelName, CancellationToken.None);
            sw.Stop();
            _out.WriteLine($"managed real load result={result} elapsed_ms={sw.ElapsedMilliseconds}");
            Assert.Equal(ModelRuntimeLoadResult.Loaded, result);
        }
        finally
        {
            try { Directory.Delete(cacheRoot, true); } catch { } // only the temp copy — never the original
        }
    }

    // ---- discovery (read-only) -----------------------------------------------------------------

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KikuCaption.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName;
    }

    private static string? FindVenvPython()
    {
        var root = RepoRoot();
        if (root is null) return null;
        var p = Path.Combine(root, "python", "whisper_worker", ".venv", "Scripts", "python.exe");
        return File.Exists(p) ? p : null;
    }

    // The first directory under models/whisper that has all four faster-whisper files + a big model.bin.
    private static string? FindValidModel()
    {
        var root = RepoRoot();
        if (root is null) return null;
        var modelsRoot = Path.Combine(root, "models", "whisper");
        if (!Directory.Exists(modelsRoot)) return null;

        foreach (var dir in Directory.EnumerateDirectories(modelsRoot, "*", SearchOption.AllDirectories))
        {
            if (new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" }.All(f => File.Exists(Path.Combine(dir, f)))
                && new FileInfo(Path.Combine(dir, "model.bin")).Length > 50_000_000)
            {
                return dir;
            }
        }

        return null;
    }

    private static WhisperWorkerOptions Worker(string venvPython) => new()
    {
        PythonExecutable = venvPython,
        WorkerScript = "unused-for-load-check"
    };

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
    }
}
