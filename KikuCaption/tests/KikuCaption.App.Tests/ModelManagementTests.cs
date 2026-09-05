using System.IO;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.App.ViewModels;
using KikuCaption.ComponentManagement.Installing;
using KikuCaption.ComponentManagement.Manifest;
using KikuCaption.ComponentManagement.Progress;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

public class ModelManagementTests
{
    private sealed class FakeCoordinator : IModelDownloadCoordinator
    {
        public bool RemoteConfigured { get; set; } = true;
        public ModelInstallStatus Status = new(false, 0, "dir", ModelVerificationLevel.None, null);
        public Func<Task>? OnInstall;
        public int InstallCalls;

        public ModelInstallStatus GetStatus(ModelCatalogEntry entry) => Status;

        public async Task InstallAsync(ModelCatalogEntry entry, IProgress<ComponentInstallProgress>? progress, CancellationToken ct)
        {
            InstallCalls++;
            progress?.Report(new ComponentInstallProgress(ComponentInstallPhases.Downloading, 0, 100));
            if (OnInstall is not null) await OnInstall();
        }
    }

    private static ModelDownloadViewModel MakeVm(FakeCoordinator coord, SessionModeState mode)
        => new(ModelCatalog.Small, coord, new LocalizationService(), mode,
            NullLogger<ModelDownloadViewModel>.Instance);

    // ---- VM: status / gating -------------------------------------------------------------------

    [Fact] // R7B.1: speech off → grey, non-blocking; but download is NOT gated by speech recognition
    public void SpeechOff_IsGrey_ButStillDownloadable()
    {
        var mode = new SessionModeState { SpeechRecognitionEnabled = false };
        var vm = MakeVm(new FakeCoordinator { RemoteConfigured = true }, mode);

        Assert.Equal("#616161", vm.StatusColor);                 // grey
        Assert.Equal(new LocalizationService()["Models.NotNeeded"], vm.StatusText);
        Assert.True(vm.CanDownload);                              // pre-install allowed even when off
    }

    [Fact] // R7B.1: no configured manifest → Download disabled + "no source" note
    public void NoManifest_DownloadDisabled_ShowsNote()
    {
        var vm = MakeVm(new FakeCoordinator { RemoteConfigured = false }, new SessionModeState());
        Assert.False(vm.CanDownload);
        Assert.True(vm.ShowNoSource);
        Assert.Equal(new LocalizationService()["Models.NoSource"], vm.NoSourceText);
    }

    [Fact] // R7B.1: installed + RuntimeVerified → green "Installed"
    public void RuntimeVerified_IsGreen()
    {
        var vm = MakeVm(new FakeCoordinator { Status = new(true, 5_000, "dir", ModelVerificationLevel.RuntimeVerified, "1") },
            new SessionModeState());
        Assert.Equal("#2E7D32", vm.StatusColor);
        Assert.Equal(new LocalizationService()["Models.Installed"], vm.StatusText);
        Assert.False(vm.CanDownload);
    }

    [Fact] // R7B.1: installed + StructureVerified → blue "downloaded, pending runtime verification"
    public void StructureVerified_IsPending_NotReady()
    {
        var vm = MakeVm(new FakeCoordinator { Status = new(true, 5_000, "dir", ModelVerificationLevel.StructureVerified, "1") },
            new SessionModeState());
        Assert.Equal("#1565C0", vm.StatusColor);
        Assert.Equal(new LocalizationService()["Models.DownloadedPendingRuntime"], vm.StatusText);
    }

    [Fact] // install success flips to installed; failure shows a code + retry
    public async Task Install_SuccessAndFailure()
    {
        var coord = new FakeCoordinator();
        coord.OnInstall = () => { coord.Status = new(true, 5_000, "dir", ModelVerificationLevel.RuntimeVerified, "1"); return Task.CompletedTask; };
        var vm = MakeVm(coord, new SessionModeState());
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.Installed);
        Assert.False(vm.HasError);

        var coord2 = new FakeCoordinator { OnInstall = () => throw new ComponentInstallException("sha256", "bad") };
        var vm2 = MakeVm(coord2, new SessionModeState());
        await vm2.DownloadCommand.ExecuteAsync(null);
        Assert.Contains("sha256", vm2.ErrorText);
        Assert.True(vm2.CanRetry);
    }

    // ---- reference-counted usage registry + guard ----------------------------------------------

    [Fact] // R7B.1: each entry point's lease keeps the model in use; guard blocks only that model
    public void Registry_Leases_BlockOnlyThatModel()
    {
        var registry = new ModelUsageRegistry();
        var guard = new WorkerModelReplacementGuard(registry);
        var small = Component("whisper-small");
        var medium = Component("whisper-medium");

        using (registry.Acquire("whisper-small", WhisperModelPurpose.Realtime))
        {
            Assert.Throws<ComponentInUseException>(() => guard.EnsureCanReplace(small)); // small blocked
            guard.EnsureCanReplace(medium); // medium free (independent)
        }

        guard.EnsureCanReplace(small); // released → allowed
    }

    [Fact] // R7B.1: two concurrent leases — replacement allowed only after BOTH release
    public void Registry_RefCount_RequiresAllReleased()
    {
        var registry = new ModelUsageRegistry();
        var lease1 = registry.Acquire("whisper-small", WhisperModelPurpose.Realtime);
        var lease2 = registry.Acquire("whisper-small", WhisperModelPurpose.WavRecognition);

        Assert.True(registry.IsInUse("whisper-small"));
        lease1.Dispose();
        Assert.True(registry.IsInUse("whisper-small")); // still one holder
        lease2.Dispose();
        Assert.False(registry.IsInUse("whisper-small"));
    }

    [Fact] // R7B.1: double-dispose does not under-count (idempotent release)
    public void Registry_DoubleDispose_IsSafe()
    {
        var registry = new ModelUsageRegistry();
        var lease = registry.Acquire("whisper-small", WhisperModelPurpose.Realtime);
        lease.Dispose();
        lease.Dispose(); // no throw, no negative count
        Assert.False(registry.IsInUse("whisper-small"));
    }

    // ---- canonical model id (case/alias cannot bypass occupancy) -------------------------------

    [Theory] // R7B.2: ids/aliases/case all canonicalize to one stable id
    [InlineData("whisper-small", "whisper-small")]
    [InlineData("Whisper-Small", "whisper-small")]
    [InlineData("small", "whisper-small")]
    [InlineData("faster-whisper-small", "whisper-small")]
    [InlineData("whisper-medium", "whisper-medium")]
    [InlineData("MEDIUM", "whisper-medium")]
    public void CanonicalModelId_MapsAliasesAndCase(string input, string expected)
        => Assert.Equal(expected, CanonicalModelId.Resolve(input));

    [Fact] // R7B.2: catalog component ids ARE the canonical ids used by the leases
    public void Catalog_ComponentIds_AreCanonical()
    {
        Assert.Equal(CanonicalModelId.Small, ModelCatalog.Small.ComponentId);
        Assert.Equal(CanonicalModelId.Medium, ModelCatalog.Medium.ComponentId);
    }

    [Fact] // R7B.2: a case/alias variant of the manifest id cannot bypass the in-use guard
    public void Guard_AliasIdCannotBypassOccupancy()
    {
        var registry = new ModelUsageRegistry();
        var guard = new WorkerModelReplacementGuard(registry);
        using (registry.Acquire(CanonicalModelId.Small, WhisperModelPurpose.Realtime))
        {
            Assert.Throws<ComponentInUseException>(() => guard.EnsureCanReplace(ComponentWithId("small")));
            Assert.Throws<ComponentInUseException>(() => guard.EnsureCanReplace(ComponentWithId("Whisper-Small")));
            guard.EnsureCanReplace(ComponentWithId("whisper-medium")); // different model still free
        }
    }

    // ---- unified locator -----------------------------------------------------------------------

    [Fact] // R7B.1: a managed small install resolves to its ABSOLUTE directory for realtime/WAV/prewarm
    public void Locator_ManagedSmall_ResolvesAbsoluteDir_AllPurposes()
    {
        var (cacheRoot, smallDir) = SeedManagedModel("faster-whisper-small", ModelVerificationLevel.RuntimeVerified, "1");
        var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);

        foreach (var purpose in new[] { WhisperModelPurpose.Realtime, WhisperModelPurpose.WavRecognition, WhisperModelPurpose.Prewarm })
        {
            var res = locator.Resolve("small", purpose);
            Assert.True(res.IsManagedInstall);
            Assert.Equal(smallDir, res.ModelName);   // absolute managed dir handed to the worker
            Assert.Equal("whisper-small", res.ModelId);
            Assert.Equal(ModelVerificationLevel.RuntimeVerified, res.VerificationLevel);
        }

        Directory.Delete(cacheRoot, true);
    }

    [Fact] // R7B.1: SpeechOptionsProvider hands the managed small dir to the worker as Model
    public void Provider_UsesManagedSmall_ForWorker()
    {
        var (cacheRoot, smallDir) = SeedManagedModel("faster-whisper-small", ModelVerificationLevel.StructureVerified, "1");
        var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);
        var provider = new SpeechOptionsProvider(new SpeechOptions { Model = "small", Language = "ja" }, contexts: null, locator);

        Assert.Equal(smallDir, provider.ForLanguage("ja", WhisperModelPurpose.Realtime).Model);
        Assert.Equal(smallDir, provider.ForLanguage("ja").Model); // default purpose also managed

        Directory.Delete(cacheRoot, true);
    }

    [Fact] // R7B.1: managed medium resolves to its absolute dir; small and medium never cross
    public void Locator_ManagedMedium_ResolvesAbsoluteDir_NoCrossover()
    {
        var (cacheRoot, mediumDir) = SeedManagedModel("faster-whisper-medium", ModelVerificationLevel.RuntimeVerified, "1");
        var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);

        var medium = locator.Resolve("medium", WhisperModelPurpose.PostMeetingCorrection);
        Assert.True(medium.IsManagedInstall);
        Assert.Equal(mediumDir, medium.ModelName);
        Assert.Equal("whisper-medium", medium.ModelId);

        var small = locator.Resolve("small", WhisperModelPurpose.Realtime);
        Assert.False(small.IsManagedInstall); // small not installed → legacy name, not the medium dir
        Assert.Equal("small", small.ModelName);

        Directory.Delete(cacheRoot, true);
    }

    [Fact] // R7B.2: an incomplete medium (no model.bin) is NOT a managed install (dir existing ≠ available)
    public void Locator_IncompleteMedium_NoModelBin_NotManaged()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "kiku_incmed_" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(cacheRoot, "faster-whisper-medium");
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "config.json", "tokenizer.json", "vocabulary.txt" })
            File.WriteAllText(Path.Combine(dir, f), "{}");
        // No model.bin.
        var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);

        var res = locator.Resolve("medium", WhisperModelPurpose.PostMeetingCorrection);
        Assert.False(res.IsManagedInstall); // falls back to legacy, not treated as installed
        Assert.False(res.Exists);
        Assert.Equal("medium", res.ModelName);

        Directory.Delete(cacheRoot, true);
    }

    [Fact] // R7B.1: no managed install → legacy model NAME is preserved
    public void Locator_NoManaged_FallsBackToLegacyName()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "kiku_loc_" + Guid.NewGuid().ToString("N"));
        var locator = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance);

        var res = locator.Resolve("small", WhisperModelPurpose.Realtime);
        Assert.False(res.IsManagedInstall);
        Assert.Equal("small", res.ModelName);
        Assert.False(res.Exists);
    }

    [Fact] // R7B.1: resolution is stable across "restarts" (a fresh locator resolves the same dir)
    public void Locator_StableAcrossRestart()
    {
        var (cacheRoot, smallDir) = SeedManagedModel("faster-whisper-small", ModelVerificationLevel.StructureVerified, "1");
        var l1 = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);
        var l2 = new WhisperModelLocator(new ModelCatalog(cacheRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);

        Assert.Equal(l1.Resolve("small", WhisperModelPurpose.Realtime).ModelName,
                     l2.Resolve("small", WhisperModelPurpose.Realtime).ModelName);
        Assert.Equal(smallDir, l2.Resolve("small", WhisperModelPurpose.Realtime).ModelName);

        Directory.Delete(cacheRoot, true);
    }

    // ---- verifier levels (fake runtime loader) -------------------------------------------------

    private sealed class FakeRuntimeLoader : IModelRuntimeLoader
    {
        private readonly ModelRuntimeLoadResult _result;
        public FakeRuntimeLoader(ModelRuntimeLoadResult result) => _result = result;
        public Task<ModelRuntimeLoadResult> TryLoadAsync(string dir, CancellationToken ct) => Task.FromResult(_result);
    }

    private static (string dir, RemoteComponent component) SeedStagingModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.bin"), new byte[20_000_000]);
        var component = new RemoteComponent("whisper-small", RemoteComponentType.WhisperModel, "1",
            "https://x/y.zip", new string('0', 64), 10, "faster-whisper-small", Array.Empty<string>());
        return (dir, component);
    }

    [Fact] // R7B.1: no healthy venv → StructureVerified (never claimed as runtime-verified)
    public async Task Verifier_NoVenv_StructureVerified()
    {
        var (dir, component) = SeedStagingModel();
        var verifier = new WhisperModelVerifier(new FakeRuntimeLoader(ModelRuntimeLoadResult.VenvUnavailable));
        Assert.Equal(ModelVerificationLevel.StructureVerified, await verifier.VerifyAsync(component, dir, CancellationToken.None));
        Directory.Delete(dir, true);
    }

    [Fact] // R7B.1: fake worker loads → RuntimeVerified
    public async Task Verifier_FakeWorkerLoads_RuntimeVerified()
    {
        var (dir, component) = SeedStagingModel();
        var verifier = new WhisperModelVerifier(new FakeRuntimeLoader(ModelRuntimeLoadResult.Loaded));
        Assert.Equal(ModelVerificationLevel.RuntimeVerified, await verifier.VerifyAsync(component, dir, CancellationToken.None));
        Directory.Delete(dir, true);
    }

    [Fact] // R7B.1: runtime load failure → throws (so a bad model cannot replace a good one)
    public async Task Verifier_RuntimeFailure_Throws()
    {
        var (dir, component) = SeedStagingModel();
        var verifier = new WhisperModelVerifier(new FakeRuntimeLoader(ModelRuntimeLoadResult.Failed));
        var ex = await Assert.ThrowsAsync<ComponentInstallException>(() => verifier.VerifyAsync(component, dir, CancellationToken.None));
        Assert.Equal("runtime-verify", ex.Code);
        Directory.Delete(dir, true);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static RemoteComponent Component(string id) => new(
        id, RemoteComponentType.WhisperModel, "1", "https://x/y.zip", new string('0', 64), 10,
        id == "whisper-small" ? "faster-whisper-small" : "faster-whisper-medium", Array.Empty<string>());

    private static RemoteComponent ComponentWithId(string id) => new(
        id, RemoteComponentType.WhisperModel, "1", "https://x/y.zip", new string('0', 64), 10,
        "faster-whisper-x", Array.Empty<string>());

    private static (string cacheRoot, string modelDir) SeedManagedModel(string subdir, ModelVerificationLevel level, string version)
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "kiku_managed_" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(cacheRoot, subdir);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "config.json", "tokenizer.json", "vocabulary.txt" })
            File.WriteAllText(Path.Combine(dir, f), "{}");
        File.WriteAllBytes(Path.Combine(dir, "model.bin"), new byte[5000]);
        InstallReceiptStore.Write(dir, new InstallReceipt(
            InstallReceiptStore.CurrentSchemaVersion, subdir.Contains("small") ? "whisper-small" : "whisper-medium",
            version, new string('0', 64), DateTime.UtcNow.ToString("O"), level));
        return (cacheRoot, dir);
    }
}
