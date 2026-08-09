using System.IO;
using KikuCaption.Core.Session;
using KikuCaption.Recording.FFmpeg;
using KikuCaption.Speech.Worker;
using KikuCaption.Storage;
using KikuCaption.Storage.Sqlite;
using KikuCaption.Translation;
using KikuCaption.Translation.Security;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Services;

/// <summary>
/// Gathers real, cheap facts and evaluates readiness before a session is created (Milestone 7 §2).
/// The pure classification lives in <see cref="PreflightEvaluator"/>; this service only collects
/// facts (file existence, disk, writability, SQLite open, translation config + DPAPI). WASAPI and
/// deep model loading are validated when capture/recognition actually start (and surfaced via the
/// state machine's rollback), which this report notes.
/// </summary>
public sealed class PreflightService
{
    private readonly StorageOptions _storage;
    private readonly RecordingRuntimeOptions _recording;
    private readonly WhisperWorkerOptions _whisper;
    private readonly TranslationOptions _translation;
    private readonly ITranslationSecretStore _secrets;
    private readonly ITranscriptStore _store;
    private readonly ILogger<PreflightService> _logger;

    public PreflightService(
        StorageOptions storage,
        RecordingRuntimeOptions recording,
        WhisperWorkerOptions whisper,
        TranslationOptions translation,
        ITranslationSecretStore secrets,
        ITranscriptStore store,
        ILogger<PreflightService> logger)
    {
        _storage = storage;
        _recording = recording;
        _whisper = whisper;
        _translation = translation;
        _secrets = secrets;
        _store = store;
        _logger = logger;
    }

    public async Task<PreflightReport> RunAsync(bool recordingRequested, string captureType, string? captureTarget, CancellationToken cancellationToken)
    {
        var root = _storage.ResolveOutputRoot();

        var inputs = new PreflightInputs
        {
            DotNetOk = true,
            PythonOk = !string.IsNullOrWhiteSpace(_whisper.PythonExecutable) && File.Exists(_whisper.PythonExecutable),
            WhisperDepsOk = !string.IsNullOrWhiteSpace(_whisper.WorkerScript) && File.Exists(_whisper.WorkerScript),
            ModelOk = ModelPresent(),
            SqliteOk = await SqliteOkAsync(cancellationToken).ConfigureAwait(false),
            WasapiDeviceOk = true, // validated at capture start; rollback handles a real failure
            OutputWritable = DirectoryWritable(root),
            DiskOk = DiskSpace.HasAtLeastGb(root, _storage.MinimumFreeSpaceGb),
            FreeDiskGb = DiskSpace.GetFreeGb(root),
            RequiredDiskGb = _storage.MinimumFreeSpaceGb,

            FfmpegOk = !recordingRequested || (_recording.FFmpegPath is not null && File.Exists(_recording.FFmpegPath)),
            FfprobeOk = !recordingRequested || FfprobeNextTo(_recording.FFmpegPath),
            EncoderOk = true, // libx264 is always available as a fallback
            CaptureTargetOk = !recordingRequested || CaptureTargetOk(captureType, captureTarget),

            TranslationEnabled = _translation.Enabled,
            TranslationConfigOk = TranslationConfigOk(),
            DpapiKeyReadable = DpapiKeyReadable()
        };

        var report = PreflightEvaluator.Evaluate(inputs);
        _logger.LogInformation("Preflight: blocking={Blocking} recording={Rec} translation={Tr}",
            report.HasBlocking, report.RecordingAvailable, report.TranslationAvailable);
        return report;
    }

    private bool ModelPresent()
    {
        var dir = _whisper.ModelCacheDirectory;
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)
                   && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return false; }
    }

    private async Task<bool> SqliteOkAsync(CancellationToken ct)
    {
        try { await _store.InitializeAsync(ct).ConfigureAwait(false); return true; }
        catch (Exception ex) { _logger.LogWarning(ex, "Preflight: SQLite not openable."); return false; }
    }

    private static bool DirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".kiku-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static bool FfprobeNextTo(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return false;
        var dir = Path.GetDirectoryName(ffmpegPath);
        return dir is not null && File.Exists(Path.Combine(dir, "ffprobe.exe"));
    }

    private static bool CaptureTargetOk(string captureType, string? target)
        => !string.Equals(captureType, "window", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(target);

    private bool TranslationConfigOk()
    {
        if (!_translation.Enabled) return false;
        return Uri.TryCreate(_translation.Endpoint, UriKind.Absolute, out var u)
               && u.Scheme == Uri.UriSchemeHttps
               && !string.IsNullOrWhiteSpace(_translation.Model);
    }

    private bool DpapiKeyReadable()
    {
        if (!_translation.Enabled) return false;
        if (_translation.AuthenticationMode == TranslationAuthMode.None) return true;
        try { return _secrets.IsConfigured && !string.IsNullOrEmpty(_secrets.Read()); }
        catch { return false; }
    }
}
