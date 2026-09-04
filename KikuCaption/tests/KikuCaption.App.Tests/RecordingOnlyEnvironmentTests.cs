using System;
using System.IO;
using System.Threading;
using KikuCaption.App.Diagnostics;
using KikuCaption.Core.Enums;
using KikuCaption.Infrastructure.Configuration;
using KikuCaption.Speech.Worker;
using Xunit;

namespace KikuCaption.App.Tests;

/// <summary>
/// UI-R6A: when local speech recognition is off, the Python/worker/model probes report a neutral
/// "skipped" row (never required, never Missing/Error) so a recording-only setup cannot show the
/// environment as broken. With recognition on they behave exactly as before (blocking when absent).
/// </summary>
public class RecordingOnlyEnvironmentTests
{
    private static WhisperWorkerOptions BogusWorker() => new()
    {
        // Deliberately non-existent so that, WHEN required, the probe would report Missing.
        PythonExecutable = Path.Combine(Path.GetTempPath(), "no_such_python_" + Guid.NewGuid().ToString("N"), "python.exe"),
        WorkerScript = Path.Combine(Path.GetTempPath(), "no_such_worker_" + Guid.NewGuid().ToString("N"), "main.py"),
        ModelCacheDirectory = Path.Combine(Path.GetTempPath(), "no_such_models_" + Guid.NewGuid().ToString("N"))
    };

    private static UserSettingsStore StoreWithSpeech(bool enabled)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiku_env", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new UserSettingsStore(dir);
        store.Save(new UserSettings { EnableSpeechRecognition = enabled });
        return store;
    }

    [Fact] // R6A-9: speech off → worker probe is skipped, not required, not missing
    public async System.Threading.Tasks.Task WorkerProbe_SpeechOff_IsSkipped()
    {
        var probe = new WhisperWorkerProbe(BogusWorker(), StoreWithSpeech(false));
        var r = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(r.Skipped);
        Assert.False(r.IsRequired);
        Assert.Equal(EnvironmentCheckStatus.Ok, r.Status);
        Assert.Equal("EnvMsg.Skipped.RecordingMode", r.MessageCode);
    }

    [Fact] // R6A-9: speech off → model probe is skipped too
    public async System.Threading.Tasks.Task ModelProbe_SpeechOff_IsSkipped()
    {
        var probe = new WhisperModelProbe(BogusWorker(), StoreWithSpeech(false));
        var r = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(r.Skipped);
        Assert.False(r.IsRequired);
        Assert.Equal(EnvironmentCheckStatus.Ok, r.Status);
    }

    [Fact] // R6A: speech ON with missing worker → required + Missing (blocks captioning as before)
    public async System.Threading.Tasks.Task WorkerProbe_SpeechOn_MissingIsRequired()
    {
        var probe = new WhisperWorkerProbe(BogusWorker(), StoreWithSpeech(true));
        var r = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(r.Skipped);
        Assert.True(r.IsRequired);
        Assert.Equal(EnvironmentCheckStatus.Missing, r.Status);
    }
}
