using KikuCaption.ComponentManagement.Archives;
using KikuCaption.ComponentManagement.Configuration;
using KikuCaption.ComponentManagement.Downloading;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Paths;
using Xunit;

namespace KikuCaption.ComponentManagement.Tests;

public class ComponentInstallCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiku_install", Guid.NewGuid().ToString("N"));

    private string ComponentsRoot => Path.Combine(_root, "components");
    private string DownloadsCache => Path.Combine(_root, "downloads");
    private string InstallDir => Path.Combine(ComponentsRoot, "small");

    private RemoteComponent ModelComponent(string sha, long size, string installDir = "small") => new(
        "whisper-small", RemoteComponentType.WhisperModel, "1", "https://x/y.zip", sha, size, installDir,
        new[] { "config.json", "model.bin", "tokenizer.json", "vocabulary.txt" });

    private ComponentInstallCoordinator Coordinator(
        FakeHttpMessageHandler handler, IComponentVerifier? verifier = null, IComponentReplacementGuard? guard = null)
    {
        var downloader = new SecureDownloader(handler.CreateTransport());
        var extractor = new SafeZipExtractor();
        var paths = new ComponentPathResolver(ComponentsRoot, DownloadsCache);
        return new ComponentInstallCoordinator(downloader, extractor, paths,
            new RemoteResourceOptions { MaxDownloadBytes = 100_000_000, MaxExtractedBytes = 100_000_000 },
            verifier, guard);
    }

    private void SeedExistingModel(string marker = "OLD")
    {
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "OLD.txt"), marker);
    }

    private bool ExistingModelIntact() => File.Exists(Path.Combine(InstallDir, "OLD.txt"));

    private static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact] // R7B: a valid model installs, reports all phases, and lands in the install directory
    public async Task Install_Success()
    {
        var (bytes, sha) = ZipFactory.Model();
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes));
        var progress = new RecordingProgress();

        var result = await coordinator.InstallAsync(ModelComponent(sha, bytes.Length), progress, CancellationToken.None);

        Assert.Equal(InstallDir, result.InstallDirectory);
        Assert.True(File.Exists(Path.Combine(InstallDir, "config.json")));
        Assert.True(File.Exists(Path.Combine(InstallDir, "model.bin")));
        Assert.Contains(ComponentInstallPhases.Completed, progress.Phases);
        Assert.Contains(ComponentInstallPhases.Downloading, progress.Phases);
        Assert.Contains(ComponentInstallPhases.Installing, progress.Phases);
        Assert.Empty(LeftoverTempDirs());
        // R7B.1: the receipt is written into the installed directory with the verification level.
        var receipt = InstallReceiptStore.TryRead(InstallDir);
        Assert.NotNull(receipt);
        Assert.Equal("whisper-small", receipt!.ComponentId);
        Assert.Equal(result.VerificationLevel, receipt.VerificationLevel);
    }

    [Fact] // R7B: a wrong SHA-256 aborts the install and leaves an existing model intact
    public async Task WrongSha_Fails_ExistingPreserved()
    {
        SeedExistingModel();
        var (bytes, _) = ZipFactory.Model();
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes));

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() =>
            coordinator.InstallAsync(ModelComponent(new string('a', 64), bytes.Length), null, CancellationToken.None));

        Assert.Equal("sha256", ex.Code);
        Assert.True(ExistingModelIntact());
        Assert.Empty(LeftoverTempDirs());
    }

    [Fact] // R7B: a ZIP missing a requiredFiles entry fails verification; existing model preserved
    public async Task MissingRequiredFile_Fails_ExistingPreserved()
    {
        SeedExistingModel();
        // Archive omits model.bin, which requiredFiles demands.
        var (bytes, sha) = ZipFactory.Build(("config.json", "{}"u8.ToArray()), ("tokenizer.json", "{}"u8.ToArray()));
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes));

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.Equal("required-file", ex.Code);
        Assert.True(ExistingModelIntact());
        Assert.Empty(LeftoverTempDirs());
    }

    [Fact] // R7B: a path-traversal ZIP is rejected during extraction; nothing is installed
    public async Task ZipTraversal_Fails()
    {
        SeedExistingModel();
        var (bytes, sha) = ZipFactory.Build(("../evil.txt", new byte[10]));
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes));

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.Equal("path", ex.Code);
        Assert.True(ExistingModelIntact());
        Assert.False(File.Exists(Path.Combine(ComponentsRoot, "evil.txt")));
    }

    [Fact] // R7B: cancellation during verify leaves no partial install and no leftover staging
    public async Task Cancellation_NoPartialInstall()
    {
        SeedExistingModel();
        var (bytes, sha) = ZipFactory.Model();
        var cancelDuringVerify = new DelegateVerifier((_, _, _) => throw new OperationCanceledException());
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes), verifier: cancelDuringVerify);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.True(ExistingModelIntact());       // never swapped
        Assert.Empty(LeftoverTempDirs());         // staging cleaned
    }

    [Fact] // R7B: the worker-in-use guard blocks replacement BEFORE any download
    public async Task WorkerInUse_Guard_Blocks_NoDownload()
    {
        SeedExistingModel();
        var (bytes, sha) = ZipFactory.Model();
        var handler = FakeHttpMessageHandler.WithBytes(bytes);
        var guard = new DelegateGuard(_ => throw new ComponentInUseException("worker busy"));
        var coordinator = Coordinator(handler, guard: guard);

        await Assert.ThrowsAsync<ComponentInUseException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount); // nothing downloaded
        Assert.True(ExistingModelIntact());
    }

    [Fact] // R7B: an atomic install replaces the old model with no .bak/.staging leftovers
    public async Task AtomicInstall_ReplacesExisting_NoLeftovers()
    {
        SeedExistingModel();
        var (bytes, sha) = ZipFactory.Model();
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes));

        await coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(InstallDir, "OLD.txt"))); // old content replaced
        Assert.True(File.Exists(Path.Combine(InstallDir, "config.json")));
        Assert.Empty(LeftoverTempDirs()); // no .bak-* or .staging-*
    }

    [Fact] // R7B: the injected verifier runs against the extracted staging (requiredFiles present there)
    public async Task Verifier_RunsAgainstExtractedFiles()
    {
        var (bytes, sha) = ZipFactory.Model();
        bool sawModelBin = false;
        var verifier = new DelegateVerifier((c, dir, _) =>
        {
            sawModelBin = File.Exists(Path.Combine(dir, "model.bin"));
            return Task.CompletedTask;
        });
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes), verifier: verifier);

        await coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None);
        Assert.True(sawModelBin);
    }

    [Fact] // R7B: a verifier rejection (e.g. light-load failure) preserves the existing model
    public async Task VerifierRejection_ExistingPreserved()
    {
        SeedExistingModel();
        var (bytes, sha) = ZipFactory.Model();
        var verifier = new DelegateVerifier((_, _, _) => throw new ComponentInstallException("verify", "bad model"));
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes), verifier: verifier);

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.Equal("verify", ex.Code);
        Assert.True(ExistingModelIntact());
        Assert.Empty(LeftoverTempDirs());
    }

    [Fact] // R7B.2: a runtime-verify failure installs nothing and writes NO receipt (never RuntimeVerified)
    public async Task RuntimeVerifyFailure_NoReceipt_NoInstall()
    {
        var (bytes, sha) = ZipFactory.Model();
        var verifier = new DelegateVerifier((_, _, _) => throw new ComponentInstallException("runtime-verify", "load failed"));
        var coordinator = Coordinator(FakeHttpMessageHandler.WithBytes(bytes), verifier: verifier);

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() =>
            coordinator.InstallAsync(ModelComponent(sha, bytes.Length), null, CancellationToken.None));

        Assert.Equal("runtime-verify", ex.Code);
        Assert.False(Directory.Exists(InstallDir));                                   // nothing installed
        Assert.False(File.Exists(Path.Combine(InstallDir, ".component.json")));       // no success receipt
        Assert.Empty(LeftoverTempDirs());
    }

    // Any .staging-* or .bak-* directory left under the components root indicates a cleanup bug.
    private IEnumerable<string> LeftoverTempDirs()
        => Directory.Exists(ComponentsRoot)
            ? Directory.EnumerateDirectories(ComponentsRoot)
                .Where(d => Path.GetFileName(d).StartsWith(".staging-") || Path.GetFileName(d).StartsWith(".bak") || Path.GetFileName(d).Contains(".bak-"))
            : Array.Empty<string>();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class DelegateVerifier : IComponentVerifier
    {
        private readonly Func<RemoteComponent, string, CancellationToken, Task> _f;
        public DelegateVerifier(Func<RemoteComponent, string, CancellationToken, Task> f) => _f = f;
        public async Task<KikuCaption.Core.Models.ModelVerificationLevel> VerifyAsync(RemoteComponent c, string dir, CancellationToken ct)
        {
            await _f(c, dir, ct);
            return KikuCaption.Core.Models.ModelVerificationLevel.StructureVerified;
        }
    }

    private sealed class DelegateGuard : IComponentReplacementGuard
    {
        private readonly Action<RemoteComponent> _f;
        public DelegateGuard(Action<RemoteComponent> f) => _f = f;
        public void EnsureCanReplace(RemoteComponent c) => _f(c);
    }
}
