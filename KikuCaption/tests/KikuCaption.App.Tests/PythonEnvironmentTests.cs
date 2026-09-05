using System.IO;
using KikuCaption.App.Localization;
using KikuCaption.App.Services;
using KikuCaption.App.Services.Python;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KikuCaption.App.Tests;

public class PythonEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kiku_py", Guid.NewGuid().ToString("N"));

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeRunner : IPythonProcessRunner
    {
        public List<(string Exe, List<string> Args)> Calls = new();
        public Func<string, IReadOnlyList<string>, (int code, string[] lines)>? OnRun;

        public Task<PythonProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? onLine, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((exe, args.ToList()));
            var (code, lines) = OnRun?.Invoke(exe, args) ?? (0, Array.Empty<string>());
            foreach (var l in lines) onLine?.Report(l);
            return Task.FromResult(new PythonProcessResult(code, string.Join("\n", lines), false));
        }
    }

    private sealed class FakeDetector : ISystemPythonDetector
    {
        public List<PythonCandidate> Candidates = new();
        public Task<IReadOnlyList<PythonCandidate>> DetectAsync(string? c, CancellationToken ct)
            => Task.FromResult((IReadOnlyList<PythonCandidate>)Candidates);
    }

    private static PythonCandidate Compatible(string exe = "C:/py/python.exe")
        => new(exe, 0, new PythonProbeInfo("3.13.9", true, true, true));

    private const string ImportsJson = "{\"py\":\"3.13.9\",\"fw\":\"1.2.1\",\"ct2\":\"4.8.1\",\"int8\":true}";

    // A runner that makes install succeed: `venv` creates a fake venv; imports return the JSON.
    private FakeRunner HappyRunner(int pipExit = 0, string? importsJson = ImportsJson, int workerExit = 0)
        => new()
        {
            OnRun = (exe, args) =>
            {
                var joined = string.Join(" ", args);
                if (args.Contains("venv")) { CreateFakeVenv(args[^1]); return (0, Array.Empty<string>()); }
                if (args.Contains("pip")) return (pipExit, Array.Empty<string>());
                if (joined.Contains("faster_whisper.__version__")) return importsJson is null ? (1, Array.Empty<string>()) : (0, new[] { importsJson });
                if (joined.Contains("import protocol")) return (workerExit, Array.Empty<string>());
                if (joined.Contains("WhisperModel")) return (0, Array.Empty<string>());
                return (0, Array.Empty<string>());
            }
        };

    private static void CreateFakeVenv(string venvDir)
    {
        Directory.CreateDirectory(Path.Combine(venvDir, "Scripts"));
        File.WriteAllText(Path.Combine(venvDir, "Scripts", "python.exe"), "");
        File.WriteAllText(Path.Combine(venvDir, "pyvenv.cfg"), "home = x");
    }

    private string WriteLock(string content = "numpy==2.5.1\nfaster-whisper==1.2.1\n# comment\n")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "requirements-lock.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private PythonEnvironmentInstaller Installer(ISystemPythonDetector detector, IPythonProcessRunner runner,
        string reqLock, IModelUsageRegistry? usage = null, IWhisperModelLocator? modelLocator = null)
        => new(detector, runner,
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            usage ?? new ModelUsageRegistry(),
            modelLocator ?? new WhisperModelLocator(new ModelCatalog(Path.Combine(_root, "models")), NullLogger<WhisperModelLocator>.Instance),
            new ModelCatalog(Path.Combine(_root, "models")),
            configuredPython: null, workerScript: "", requirementsLock: reqLock,
            NullLogger<PythonEnvironmentInstaller>.Instance, pythonRoot: Path.Combine(_root, "pyroot"));

    private string Managed => Path.Combine(_root, "pyroot", "venv");
    private static string VenvPy(string venv) => Path.Combine(venv, "Scripts", "python.exe");

    // ---- detector ------------------------------------------------------------------------------

    [Fact] // R7C: picks the highest-priority COMPATIBLE candidate; rejects incompatible + x86
    public void Detector_PicksBestCompatible()
    {
        var all = new List<PythonCandidate>
        {
            new("a", 2, new PythonProbeInfo("3.10.0", true, true, false)),  // too old
            new("b", 1, new PythonProbeInfo("3.13.9", false, true, false)), // x86
            new("c", 0, new PythonProbeInfo("3.13.5", true, true, true)),   // compatible, best priority
            new("d", 3, new PythonProbeInfo("3.12.1", true, true, true)),   // compatible, worse priority
        };
        var best = SystemPythonDetector.Best(all.OrderByDescending(x => x.Info!.Compatible).ThenBy(x => x.Priority).ToList());
        Assert.Equal("c", best!.Executable);
    }

    [Theory] // version gate: only 3.12–3.13 x64 with venv is compatible
    [InlineData(3, 13, true, true)]
    [InlineData(3, 12, true, true)]
    [InlineData(3, 11, false, false)]
    [InlineData(3, 14, false, false)]
    public void PythonPaths_VersionGate(int major, int minor, bool x64Ignored, bool expected)
        => Assert.Equal(expected, PythonPaths.IsSupportedVersion(major, minor));

    [Fact] // R7C: a Windows Store alias (probe produces no JSON) is discarded, not crashed
    public async Task Detector_StoreAliasProducesNoCandidate()
    {
        var fakeExe = Path.Combine(_root, "python.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllText(fakeExe, "");
        var runner = new FakeRunner { OnRun = (_, _) => (9009, Array.Empty<string>()) }; // alias-like failure
        var detector = new SystemPythonDetector(runner, NullLogger<SystemPythonDetector>.Instance);

        var result = await detector.DetectAsync(fakeExe, CancellationToken.None);
        Assert.All(result, c => Assert.False(c.Info?.Compatible == true));
        Assert.Null(SystemPythonDetector.Best(result));
    }

    // ---- locator -------------------------------------------------------------------------------

    [Fact] // R7C: resolution order — config venv wins; none → None
    public void Locator_ResolvesConfigAndNone()
    {
        var venv = Path.Combine(_root, "cfgvenv");
        CreateFakeVenv(venv);
        var withConfig = new PythonEnvironmentLocator(VenvPy(venv), null, null, NullLogger<PythonEnvironmentLocator>.Instance).Resolve();
        Assert.Equal(PythonEnvSource.Config, withConfig.WorkerSource);
        Assert.Equal(VenvPy(venv), withConfig.WorkerPython);

        var none = new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance).Resolve();
        Assert.Equal(PythonEnvSource.None, none.WorkerSource);
        Assert.Null(none.WorkerPython);
    }

    [Fact] // R7C: a dev venv resolves as DevVenv and is NOT deleted by anything here
    public void Locator_ResolvesDevVenv_Legacy()
    {
        var dev = Path.Combine(_root, "devvenv");
        CreateFakeVenv(dev);
        var r = new PythonEnvironmentLocator(null, VenvPy(dev), null, NullLogger<PythonEnvironmentLocator>.Instance).Resolve();
        Assert.Equal(PythonEnvSource.DevVenv, r.WorkerSource);
        Assert.True(File.Exists(VenvPy(dev))); // untouched
    }

    // ---- installer -----------------------------------------------------------------------------

    [Fact] // R7C: no venv → install creates the managed venv atomically, reports phases
    public async Task Install_Success_CreatesManagedVenv()
    {
        var detector = new FakeDetector { Candidates = { Compatible() } };
        var runner = HappyRunner();
        var phases = new List<PythonInstallPhase>();
        var progress = new Progress<PythonInstallProgress>(p => phases.Add(p.Phase));

        var result = await Installer(detector, runner, WriteLock()).InstallOrRepairAsync(progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("1.2.1", result.FasterWhisperVersion);
        Assert.True(File.Exists(VenvPy(Managed)));
        Assert.Empty(LeftoverTemp());
    }

    [Fact] // R7C: pip failure aborts and preserves an existing managed venv
    public async Task PipFailure_PreservesExisting()
    {
        CreateFakeVenv(Managed);
        File.WriteAllText(Path.Combine(Managed, "MARKER.txt"), "old");
        var detector = new FakeDetector { Candidates = { Compatible() } };

        var result = await Installer(detector, HappyRunner(pipExit: 1), WriteLock()).InstallOrRepairAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("pip", result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(Managed, "MARKER.txt"))); // old venv intact
        Assert.Empty(LeftoverTemp());
    }

    [Fact] // R7C: import verification failure preserves the old venv
    public async Task ImportFailure_PreservesExisting()
    {
        CreateFakeVenv(Managed);
        File.WriteAllText(Path.Combine(Managed, "MARKER.txt"), "old");
        var detector = new FakeDetector { Candidates = { Compatible() } };

        var result = await Installer(detector, HappyRunner(importsJson: null), WriteLock()).InstallOrRepairAsync(null, CancellationToken.None);

        Assert.Equal("imports", result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(Managed, "MARKER.txt")));
    }

    [Fact] // R7C: worker self-check failure preserves the old venv
    public async Task WorkerFailure_PreservesExisting()
    {
        CreateFakeVenv(Managed);
        File.WriteAllText(Path.Combine(Managed, "MARKER.txt"), "old");
        var detector = new FakeDetector { Candidates = { Compatible() } };
        // Give a worker script so the self-check actually runs and can fail.
        var installer = new PythonEnvironmentInstaller(detector, HappyRunner(workerExit: 1),
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            new ModelUsageRegistry(),
            new WhisperModelLocator(new ModelCatalog(Path.Combine(_root, "models")), NullLogger<WhisperModelLocator>.Instance),
            new ModelCatalog(Path.Combine(_root, "models")),
            null, WriteWorkerScript(), WriteLock(), NullLogger<PythonEnvironmentInstaller>.Instance, Path.Combine(_root, "pyroot"));

        var result = await installer.InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("worker", result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(Managed, "MARKER.txt")));
    }

    [Fact] // R7C: no compatible system python → cannot install
    public async Task NoSystemPython_Fails()
    {
        var result = await Installer(new FakeDetector(), HappyRunner(), WriteLock()).InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("no-python", result.ErrorCode);
    }

    [Fact] // R7C: requirements-lock not fully pinned → rejected
    public async Task UnpinnedRequirements_Fails()
    {
        var detector = new FakeDetector { Candidates = { Compatible() } };
        var result = await Installer(detector, HappyRunner(), WriteLock("numpy>=2.0\n")).InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("requirements", result.ErrorCode);
    }

    [Fact] // R7C: while the worker is using a model, install/switch is refused (no runner calls)
    public async Task WorkerInUse_Blocks()
    {
        var registry = new ModelUsageRegistry();
        using var lease = registry.Acquire(ModelCatalog.Small.ComponentId, WhisperModelPurpose.Realtime);
        var detector = new FakeDetector { Candidates = { Compatible() } };
        var runner = HappyRunner();

        var result = await Installer(detector, runner, WriteLock(), usage: registry).InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("in-use", result.ErrorCode);
        Assert.Empty(runner.Calls); // nothing ran
    }

    [Fact] // R7C: an atomic install replaces the old venv with no .backup/.staging leftovers
    public async Task AtomicInstall_ReplacesExisting_NoLeftovers()
    {
        CreateFakeVenv(Managed);
        File.WriteAllText(Path.Combine(Managed, "MARKER.txt"), "old");
        var detector = new FakeDetector { Candidates = { Compatible() } };

        var result = await Installer(detector, HappyRunner(), WriteLock()).InstallOrRepairAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(Managed, "MARKER.txt"))); // replaced
        Assert.True(File.Exists(VenvPy(Managed)));
        Assert.Empty(LeftoverTemp());
    }

    [Fact] // R7C: after install, an installed StructureVerified model is upgraded to RuntimeVerified
    public async Task Install_UpgradesModelToRuntimeVerified()
    {
        var modelsRoot = Path.Combine(_root, "models");
        var smallDir = Path.Combine(modelsRoot, "faster-whisper-small");
        Directory.CreateDirectory(smallDir);
        foreach (var f in new[] { "config.json", "tokenizer.json", "vocabulary.txt" }) File.WriteAllText(Path.Combine(smallDir, f), "{}");
        File.WriteAllBytes(Path.Combine(smallDir, "model.bin"), new byte[20_000_000]);
        KikuCaption.ComponentManagement.Installing.InstallReceiptStore.Write(smallDir, new KikuCaption.ComponentManagement.Installing.InstallReceipt(
            1, "whisper-small", "1", new string('0', 64), DateTime.UtcNow.ToString("O"), ModelVerificationLevel.StructureVerified));

        var modelLocator = new WhisperModelLocator(new ModelCatalog(modelsRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000);
        var detector = new FakeDetector { Candidates = { Compatible() } };
        var installer = new PythonEnvironmentInstaller(detector, HappyRunner(),
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            new ModelUsageRegistry(), modelLocator, new ModelCatalog(modelsRoot),
            null, "", WriteLock(), NullLogger<PythonEnvironmentInstaller>.Instance, Path.Combine(_root, "pyroot"));

        var result = await installer.InstallOrRepairAsync(null, CancellationToken.None);
        Assert.True(result.Success);
        var receipt = KikuCaption.ComponentManagement.Installing.InstallReceiptStore.TryRead(smallDir);
        Assert.Equal(ModelVerificationLevel.RuntimeVerified, receipt!.VerificationLevel);
    }

    [Fact] // R7C: a second concurrent install is refused ("busy") while the first holds the lock
    public async Task ConcurrentInstall_Rejected()
    {
        var gate = new TaskCompletionSource();
        var atVenv = new TaskCompletionSource();
        // A runner whose venv step blocks OFF-THREAD until released (so the first install task is pending).
        var runner = new BlockingRunner(gate, atVenv);
        var detector = new FakeDetector { Candidates = { Compatible() } };
        var installer = Installer(detector, runner, WriteLock());

        var first = installer.InstallOrRepairAsync(null, CancellationToken.None); // pending inside venv
        await atVenv.Task; // the first install now holds the single-task lock
        var second = await installer.InstallOrRepairAsync(null, CancellationToken.None); // should be busy
        Assert.Equal("busy", second.ErrorCode);

        gate.SetResult();
        Assert.True((await first).Success);
    }

    // A runner whose venv step signals + awaits a gate on a background thread (for the busy test).
    private sealed class BlockingRunner : IPythonProcessRunner
    {
        private readonly TaskCompletionSource _gate;
        private readonly TaskCompletionSource _atVenv;
        public BlockingRunner(TaskCompletionSource gate, TaskCompletionSource atVenv) { _gate = gate; _atVenv = atVenv; }

        public async Task<PythonProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? onLine, CancellationToken ct)
        {
            if (args.Contains("venv"))
            {
                CreateFakeVenv(args[^1]);
                _atVenv.TrySetResult();
                await _gate.Task.ConfigureAwait(false); // yields the thread; first install stays pending
                return new PythonProcessResult(0, "", false);
            }

            if (string.Join(" ", args).Contains("faster_whisper.__version__"))
            {
                onLine?.Report(ImportsJson); // the installer reads the JSON from the output line
                return new PythonProcessResult(0, ImportsJson, false);
            }

            return new PythonProcessResult(0, "", false);
        }
    }

    // ---- view model ----------------------------------------------------------------------------

    private sealed class FakeInstaller : IPythonEnvironmentInstaller
    {
        public PythonEnvironmentStatus Status = new(PythonEnvState.NoVenv, "3.13.9", null, null, null);
        public PythonEnvironmentInstallResult Result = new(true, PythonInstallPhase.Completed, null, "3.13.9", "1.2.1", "4.8.1");
        public Func<Task>? OnInstall;
        public Task<PythonEnvironmentStatus> InspectAsync(CancellationToken ct) => Task.FromResult(Status);
        public async Task<PythonEnvironmentInstallResult> InstallOrRepairAsync(IProgress<PythonInstallProgress>? p, CancellationToken ct)
        { if (OnInstall is not null) await OnInstall(); return Result; }
    }

    private static PythonEnvironmentViewModel Vm(FakeInstaller installer, SessionModeState mode)
        => new(installer, new LocalizationService(), mode, NullLogger<PythonEnvironmentViewModel>.Instance);

    [Fact] // R7C: speech off → grey non-blocking, but the user may still install
    public async Task Vm_SpeechOff_GreyButInstallable()
    {
        var vm = Vm(new FakeInstaller(), new SessionModeState { SpeechRecognitionEnabled = false });
        await vm.RefreshAsync();
        Assert.Equal("#616161", vm.StatusColor);
        Assert.Equal(new LocalizationService()["Py.NotNeeded"], vm.StatusText);
        Assert.True(vm.CanInstall); // a compatible system python was detected
    }

    [Fact] // R7C: a successful install raises EnvironmentChanged (page re-checks probes + models)
    public async Task Vm_Install_RaisesEnvironmentChanged()
    {
        var installer = new FakeInstaller();
        installer.OnInstall = () => { installer.Status = new(PythonEnvState.VenvHealthy, "3.13.9", "3.13.9", "1.2.1", "4.8.1"); return Task.CompletedTask; };
        var vm = Vm(installer, new SessionModeState());
        await vm.RefreshAsync();

        bool raised = false;
        vm.EnvironmentChanged += (_, _) => raised = true;
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.True(raised);
        Assert.Equal(PythonEnvState.VenvHealthy, vm.State);
        Assert.True(vm.HasVersions);
    }

    [Fact] // R7C: no compatible system python → shows the download-guide affordance, not install
    public async Task Vm_NoSystemPython_ShowsGuide()
    {
        var vm = Vm(new FakeInstaller { Status = new(PythonEnvState.NoSystemPython, null, null, null, null) }, new SessionModeState());
        await vm.RefreshAsync();
        Assert.True(vm.ShowNoSystemPython);
        Assert.False(vm.CanInstall);
    }

    // ---- process runner scrubbing --------------------------------------------------------------

    [Fact] // R7C: URL query/token is scrubbed from the diagnostic output tail (never logged raw)
    public async Task Runner_ScrubsUrlTokensFromOutput()
    {
        var runner = new PythonProcessRunner();
        var lines = new List<string>();
        var r = await runner.RunAsync("cmd", new[] { "/c", "echo", "https://pypi.example/simple?token=SECRET123" },
            new Progress<string>(lines.Add), CancellationToken.None);

        var all = r.TailOutput + string.Join("\n", lines);
        Assert.Contains("pypi.example", all);
        Assert.DoesNotContain("SECRET123", all);
        Assert.DoesNotContain("token", all);
    }

    // ---- R7C.1: crash recovery -----------------------------------------------------------------

    private string PyRoot => Path.Combine(_root, "pyroot");

    // A recovery runner that reports a venv healthy iff its dir name is in the healthy set.
    private sealed class RecoveryRunner : IPythonProcessRunner
    {
        private readonly Func<string, bool> _healthy;
        public RecoveryRunner(Func<string, bool> healthy) => _healthy = healthy;
        public Task<PythonProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? onLine, CancellationToken ct)
        {
            if (string.Join(" ", args).Contains("faster_whisper"))
            {
                var ok = _healthy(exe);
                if (ok) onLine?.Report(ImportsJson);
                return Task.FromResult(new PythonProcessResult(ok ? 0 : 1, ok ? ImportsJson : "", false));
            }
            return Task.FromResult(new PythonProcessResult(0, "", false));
        }
    }

    private PythonEnvironmentRecovery Recovery(Func<string, bool> healthy)
        => new(new RecoveryRunner(healthy), NullLogger<PythonEnvironmentRecovery>.Instance, PyRoot);

    private static void MarkVenv(string venvDir, string marker)
    {
        CreateFakeVenv(venvDir);
        File.WriteAllText(Path.Combine(venvDir, "MARKER.txt"), marker);
    }

    private static string ReadMarker(string venvDir)
        => File.Exists(Path.Combine(venvDir, "MARKER.txt")) ? File.ReadAllText(Path.Combine(venvDir, "MARKER.txt")) : "";

    private void WriteJournal(PythonInstallTxPhase phase, string? backup, string? staging)
    {
        Directory.CreateDirectory(PyRoot);
        var entry = new PythonInstallJournalEntry(1, "op", phase, Managed, staging ?? Path.Combine(PyRoot, ".venv-staging-x"), backup, DateTime.UtcNow.ToString("O"));
        File.WriteAllText(Path.Combine(PyRoot, "install-state.json"),
            System.Text.Json.JsonSerializer.Serialize(entry));
    }

    private string StagingDir(string suffix = "aaa") => Path.Combine(PyRoot, ".venv-staging-" + suffix);
    private string BackupDir(string suffix = "aaa") => Path.Combine(PyRoot, ".venv-backup-" + suffix);

    [Fact] // R7C.1 (#1): killed after old→backup → restart restores the old environment
    public async Task Recovery_RestoresBackup_WhenManagedMissing()
    {
        MarkVenv(BackupDir(), "old");
        MarkVenv(StagingDir(), "new");        // a staging that was mid-flight
        WriteJournal(PythonInstallTxPhase.OldEnvironmentBackedUp, BackupDir(), StagingDir());

        var result = await Recovery(_ => true).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.RestoredBackup, result.Action);
        Assert.True(result.ManagedUsable);
        Assert.Equal("old", ReadMarker(Managed));
        Assert.False(Directory.Exists(BackupDir()));
        Assert.Empty(LeftoverTemp()); // stray staging cleaned
        Assert.False(File.Exists(Path.Combine(PyRoot, "install-state.json")));
    }

    [Fact] // R7C.1 (#2): killed after staging→managed, before backup delete → verify + keep the new env
    public async Task Recovery_KeepsNewManaged_AndDropsBackup()
    {
        MarkVenv(Managed, "new");
        MarkVenv(BackupDir(), "old");
        WriteJournal(PythonInstallTxPhase.NewEnvironmentActivated, BackupDir(), null);

        var result = await Recovery(_ => true).RecoverAsync(CancellationToken.None); // managed verifies healthy

        Assert.Equal(PythonRecoveryAction.KeptManaged, result.Action);
        Assert.Equal("new", ReadMarker(Managed));
        Assert.False(Directory.Exists(BackupDir()));
        Assert.Empty(LeftoverTemp());
    }

    [Fact] // R7C.1 (#3): a new managed that fails runtime verification rolls back to the backup
    public async Task Recovery_RollsBackToBackup_WhenManagedInvalid()
    {
        MarkVenv(Managed, "new-broken");
        MarkVenv(BackupDir(), "old");
        // Only the backup dir verifies healthy; the managed dir does not.
        var result = await Recovery(exe => exe.Contains(".venv-backup-")).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.RolledBackToBackup, result.Action);
        Assert.Equal("old", ReadMarker(Managed));       // backup restored as managed
        Assert.True(result.ManagedUsable);
        Assert.Contains(Directory.EnumerateDirectories(PyRoot), d => Path.GetFileName(d).StartsWith(".venv-quarantine-")); // old broken managed kept
    }

    [Fact] // R7C.1 (#4): a stray staging next to a valid managed is cleaned; managed is never overwritten
    public async Task Recovery_CleansStrayStaging_KeepsValidManaged()
    {
        MarkVenv(Managed, "good");
        MarkVenv(StagingDir(), "leftover");
        // Only the managed venv verifies healthy (its path is not a staging dir).
        var result = await Recovery(exe => !exe.Contains(".venv-staging-")).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.KeptManaged, result.Action);
        Assert.Equal("good", ReadMarker(Managed));
        Assert.Empty(LeftoverTemp()); // staging removed
    }

    [Fact] // R7C.1 (#5): a corrupt install-state.json is quarantined and recovery is conservative (no crash)
    public async Task Recovery_QuarantinesCorruptJournal_AndRecoversConservatively()
    {
        MarkVenv(Managed, "good");
        Directory.CreateDirectory(PyRoot);
        File.WriteAllText(Path.Combine(PyRoot, "install-state.json"), "{ this is : not valid json ]");

        var result = await Recovery(_ => true).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.JournalCorrupted, result.Action);
        Assert.True(result.ManagedUsable);              // managed left intact
        Assert.Equal("good", ReadMarker(Managed));
        Assert.Contains(Directory.EnumerateFiles(PyRoot), f => Path.GetFileName(f).StartsWith("install-state.json.corrupt-"));
        Assert.False(File.Exists(Path.Combine(PyRoot, "install-state.json")));
    }

    [Fact] // R7C.1 (#11): recovery never deletes the only valid venv (promotes a valid staging over a broken managed)
    public async Task Recovery_DoesNotDeleteTheOnlyValidVenv()
    {
        MarkVenv(Managed, "broken");
        MarkVenv(StagingDir(), "the-only-good-one");
        // Only the staging verifies healthy; managed is broken; no backup exists.
        var result = await Recovery(exe => exe.Contains(".venv-staging-")).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.PromotedStaging, result.Action);
        Assert.Equal("the-only-good-one", ReadMarker(Managed)); // the sole valid venv survived, as managed
        Assert.True(result.ManagedUsable);
    }

    [Fact] // R7C.1 (#12): after recovery the on-disk state is truthful (probe would see the real thing)
    public async Task Recovery_LeavesTruthfulState()
    {
        // managed gone, only an UNVERIFIABLE staging → recovery must NOT fabricate a managed env.
        MarkVenv(StagingDir(), "unverified");
        var result = await Recovery(_ => false).RecoverAsync(CancellationToken.None);

        Assert.Equal(PythonRecoveryAction.QuarantinedStaging, result.Action);
        Assert.False(result.ManagedUsable);
        Assert.False(PythonPaths.VenvExists(Managed)); // a probe would honestly report "not installed"
    }

    // ---- R7C.1: cross-process lock -------------------------------------------------------------

    [Fact] // R7C.1 (#8): two installer instances race; only one gets the cross-process lock, the other is "locked"
    public async Task CrossProcess_SecondInstallerIsLocked()
    {
        var lockName = @"Local\KikuTest_" + Guid.NewGuid().ToString("N");
        var gate = new TaskCompletionSource();
        var atVenv = new TaskCompletionSource();
        var detector = new FakeDetector { Candidates = { Compatible() } };

        var first = InstallerWithLock(detector, new BlockingRunner(gate, atVenv), WriteLock(), lockName);
        var second = InstallerWithLock(new FakeDetector { Candidates = { Compatible() } }, HappyRunner(), WriteLock(), lockName);

        var firstRun = first.InstallOrRepairAsync(null, CancellationToken.None); // holds the lock at the venv step
        await atVenv.Task;
        var secondRun = await second.InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("locked", secondRun.ErrorCode);

        gate.SetResult();
        Assert.True((await firstRun).Success);
    }

    [Fact] // R7C.1 (#9): waiting on the lock exits promptly when cancelled
    public async Task CrossProcess_WaitCancels()
    {
        var lockName = @"Local\KikuTest_" + Guid.NewGuid().ToString("N");
        var a = new CrossProcessInstallLock(lockName);
        var b = new CrossProcessInstallLock(lockName);

        using var held = await a.TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);

        using var cts = new CancellationTokenSource();
        var waiting = b.TryAcquireAsync(Timeout.InfiniteTimeSpan, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);

        held!.Dispose();
        using var now = await b.TryAcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None); // free now
        Assert.NotNull(now);
    }

    [Fact] // R7C.1: an abandoned mutex (owner exited without releasing) is still re-acquirable (crash-safe)
    public async Task CrossProcess_AbandonedLockIsReacquired()
    {
        var lockName = @"Local\KikuTest_" + Guid.NewGuid().ToString("N");
        // A thread acquires the raw named mutex and exits without releasing → the OS abandons it.
        var owner = new Thread(() => { using var raw = new Mutex(false, lockName); raw.WaitOne(0); });
        owner.Start();
        owner.Join();
        // A fresh acquire must still succeed (AbandonedMutexException is treated as ownership).
        using var reacquired = await new CrossProcessInstallLock(lockName).TryAcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(reacquired);
    }

    [Fact] // R7C.1 (#10): setup-python.ps1 refuses to run while the app holds the shared install lock
    public async Task SetupScript_RefusesWhileAppInstalling()
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = LocateSetupScript();
        if (script is null) return; // not in a repo checkout (e.g. packaged test run) → skip

        var lockName = @"Local\KikuTest_" + Guid.NewGuid().ToString("N");
        using var held = await new CrossProcessInstallLock(lockName).TryAcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held); // the "app" now holds the install lock

        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                  "-File", script, "-LockName", lockName, "-VenvTarget", Path.Combine(_root, "should-not-be-created") })
            psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        Assert.NotEqual(0, p.ExitCode); // refused
        Assert.False(Directory.Exists(Path.Combine(_root, "should-not-be-created")), "the script must not touch the venv while locked");
    }

    private static string? LocateSetupScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "setup-python.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // ---- R7C.1: model-receipt commit ordering --------------------------------------------------

    private (string smallDir, WhisperModelLocator locator, ModelCatalog catalog) SeedStructureVerifiedSmall()
    {
        var modelsRoot = Path.Combine(_root, "models");
        var smallDir = Path.Combine(modelsRoot, "faster-whisper-small");
        Directory.CreateDirectory(smallDir);
        foreach (var f in new[] { "config.json", "tokenizer.json", "vocabulary.txt" }) File.WriteAllText(Path.Combine(smallDir, f), "{}");
        File.WriteAllBytes(Path.Combine(smallDir, "model.bin"), new byte[20_000_000]);
        KikuCaption.ComponentManagement.Installing.InstallReceiptStore.Write(smallDir, new KikuCaption.ComponentManagement.Installing.InstallReceipt(
            1, "whisper-small", "1", new string('0', 64), DateTime.UtcNow.ToString("O"), ModelVerificationLevel.StructureVerified));
        return (smallDir, new WhisperModelLocator(new ModelCatalog(modelsRoot), NullLogger<WhisperModelLocator>.Instance, minModelBytes: 1000), new ModelCatalog(modelsRoot));
    }

    private static ModelVerificationLevel? ReceiptLevel(string dir)
        => KikuCaption.ComponentManagement.Installing.InstallReceiptStore.TryRead(dir)?.VerificationLevel;

    [Fact] // R7C.1 (#6): a commit (rename) failure does NOT upgrade the model receipt
    public async Task CommitFailure_DoesNotUpgradeReceipt()
    {
        var (smallDir, modelLocator, catalog) = SeedStructureVerifiedSmall();
        // Put a FILE where the managed venv dir should go → Directory.Move(staging, managed) throws.
        Directory.CreateDirectory(PyRoot);
        File.WriteAllText(Managed, "not-a-directory");

        var detector = new FakeDetector { Candidates = { Compatible() } };
        var installer = new PythonEnvironmentInstaller(detector, HappyRunner(),
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            new ModelUsageRegistry(), modelLocator, catalog,
            null, "", WriteLock(), NullLogger<PythonEnvironmentInstaller>.Instance, PyRoot);

        var result = await installer.InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("commit", result.ErrorCode);
        Assert.Equal(ModelVerificationLevel.StructureVerified, ReceiptLevel(smallDir)); // NOT upgraded
    }

    [Fact] // R7C.1 (#7): a post-activation runtime failure rolls back and does NOT upgrade the receipt
    public async Task PostActivationFailure_RollsBack_NoReceiptUpgrade()
    {
        var (smallDir, modelLocator, catalog) = SeedStructureVerifiedSmall();
        MarkVenv(Managed, "old");

        // Imports pass under staging, but FAIL once the venv is the managed dir (post-activation verify).
        var runner = new FakeRunner
        {
            OnRun = (exe, args) =>
            {
                var joined = string.Join(" ", args);
                if (args.Contains("venv")) { CreateFakeVenv(args[^1]); return (0, Array.Empty<string>()); }
                if (args.Contains("pip")) return (0, Array.Empty<string>());
                if (joined.Contains("faster_whisper.__version__"))
                {
                    var underStaging = exe.Contains(".venv-staging-");
                    return underStaging ? (0, new[] { ImportsJson }) : (1, Array.Empty<string>());
                }
                return (0, Array.Empty<string>());
            }
        };

        var detector = new FakeDetector { Candidates = { Compatible() } };
        var installer = new PythonEnvironmentInstaller(detector, runner,
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            new ModelUsageRegistry(), modelLocator, catalog,
            null, "", WriteLock(), NullLogger<PythonEnvironmentInstaller>.Instance, PyRoot);

        var result = await installer.InstallOrRepairAsync(null, CancellationToken.None);
        Assert.Equal("post-verify", result.ErrorCode);
        Assert.Equal("old", ReadMarker(Managed));                                     // rolled back
        Assert.Equal(ModelVerificationLevel.StructureVerified, ReceiptLevel(smallDir)); // NOT upgraded
        Assert.Empty(LeftoverTemp());
    }

    private PythonEnvironmentInstaller InstallerWithLock(ISystemPythonDetector detector, IPythonProcessRunner runner, string reqLock, string lockName)
        => new(detector, runner,
            new PythonEnvironmentLocator(null, null, null, NullLogger<PythonEnvironmentLocator>.Instance),
            new ModelUsageRegistry(),
            new WhisperModelLocator(new ModelCatalog(Path.Combine(_root, "models")), NullLogger<WhisperModelLocator>.Instance),
            new ModelCatalog(Path.Combine(_root, "models")),
            configuredPython: null, workerScript: "", requirementsLock: reqLock,
            NullLogger<PythonEnvironmentInstaller>.Instance, pythonRoot: PyRoot,
            installLock: new CrossProcessInstallLock(lockName));

    // ---- helpers -------------------------------------------------------------------------------

    private string WriteWorkerScript()
    {
        var dir = Path.Combine(_root, "worker");
        Directory.CreateDirectory(dir);
        var main = Path.Combine(dir, "main.py");
        File.WriteAllText(main, "# worker");
        return main;
    }

    private IEnumerable<string> LeftoverTemp()
    {
        var pyroot = Path.Combine(_root, "pyroot");
        return Directory.Exists(pyroot)
            ? Directory.EnumerateDirectories(pyroot).Where(d => Path.GetFileName(d).StartsWith(".venv-"))
            : Array.Empty<string>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
