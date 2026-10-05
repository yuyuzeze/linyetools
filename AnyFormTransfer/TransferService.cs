using System.Text.Json;

namespace AnyFormTransfer;

public sealed class TransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly TransferOptions _options;
    private readonly TransferLog _log;
    private readonly IBlobUploader _uploader;

    public TransferService(TransferOptions options, TransferLog log, IBlobUploader uploader)
    {
        _options = options;
        _log = log;
        _uploader = uploader;
    }

    public async Task<TransferSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.OutputFolder);
        Directory.CreateDirectory(_options.ProcessedFolder);
        Directory.CreateDirectory(_options.RetryFolder);
        var logDir = Path.GetDirectoryName(Path.GetFullPath(_options.LogFilePath));
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        await using var hold = await TryAcquireLockAsync(cancellationToken).ConfigureAwait(false);
        if (hold is null)
        {
            return new TransferSummary { LockTimedOut = true };
        }

        return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransferSummary> RunCoreAsync(CancellationToken cancellationToken)
    {
        var sasState = string.IsNullOrWhiteSpace(_options.Sas) ? "未設定" : "設定あり";
        _log.Info(
            $"転送開始 コンテナ={_options.ContainerName} SAS={sasState} リトライ上限={_options.MaxRetry} " +
            $"出力={_options.OutputFolder} 処理済={_options.ProcessedFolder}");
        _log.Info("Azure Blob へは接続しません。対象ファイルをログに記録し、成功分を処理済フォルダへ移動します。");

        var succeeded = new List<string>();
        var failed = 0;
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 今回失敗したファイルは再スキャンで即座に再送しない。次回起動（次のエクスポートまたは手動実行）で継続する。
        var failedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = ListPending()
                .Where(path => !stalled.Contains(path) && !failedThisRun.Contains(path))
                .ToList();
            var actionable = new List<string>();

            foreach (var path in pending)
            {
                var attempts = ReadAttempts(path);
                if (attempts >= _options.MaxRetry)
                {
                    if (skipped.Add(path))
                    {
                        _log.Warn($"スキップ（リトライ上限超過、フォルダに残置）: {path} 失敗回数={attempts}");
                    }
                }
                else
                {
                    actionable.Add(path);
                }
            }

            if (actionable.Count == 0)
            {
                break;
            }

            var progress = false;
            foreach (var path in actionable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await ProcessOneAsync(path, cancellationToken).ConfigureAwait(false);
                switch (outcome.Kind)
                {
                    case ProcessKind.Succeeded:
                        succeeded.Add(outcome.Detail);
                        progress = true;
                        break;
                    case ProcessKind.Failed:
                        failed++;
                        progress = true;
                        if (outcome.Detail.Length > 0)
                        {
                            failedThisRun.Add(outcome.Detail);
                        }
                        break;
                    case ProcessKind.Stalled:
                        stalled.Add(path);
                        failed++;
                        break;
                }
            }

            if (!progress)
            {
                break;
            }

            if (_options.RescanDelayMs > 0)
            {
                await Task.Delay(_options.RescanDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        _log.Info($"処理完了 成功={succeeded.Count} 失敗={failed} スキップ={skipped.Count}");
        if (succeeded.Count == 0)
        {
            _log.Info("処理したファイル: なし");
        }
        else
        {
            _log.Info("処理したファイル:");
            foreach (var name in succeeded)
            {
                _log.Info("  " + name);
            }
        }

        return new TransferSummary
        {
            Succeeded = succeeded.Count,
            Failed = failed,
            Skipped = skipped.Count,
            ProcessedFiles = succeeded
        };
    }

    private async Task<ProcessOutcome> ProcessOneAsync(string path, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var blobName = $"{_options.ContainerName}/{fileName}";
        _log.Info($"処理対象: {path}");

        UploadOutcome uploaded;
        try
        {
            uploaded = await _uploader.UploadAsync(
                new BlobUploadRequest(path, blobName, _options.Sas),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            uploaded = new UploadOutcome(false, ex.Message);
        }

        if (!uploaded.Succeeded)
        {
            var attempts = ReadAttempts(path) + 1;
            if (!TryMoveToRetry(path, out var parked, out var moveError))
            {
                _log.Error($"転送失敗、リトライフォルダへ移動できません: {path} / {moveError}");
                return new ProcessOutcome(ProcessKind.Stalled, "");
            }

            WriteAttempts(parked, attempts, uploaded.Message);
            _log.Error($"転送失敗 ({attempts}/{_options.MaxRetry}): {fileName} -> {parked} / {uploaded.Message}");
            return new ProcessOutcome(ProcessKind.Failed, Path.GetFullPath(parked));
        }

        if (!TryMoveToProcessed(path, out var dest, out var error))
        {
            _log.Error($"転送記録は成功しましたが、処理済フォルダへ移動できません: {path} / {error}");
            return new ProcessOutcome(ProcessKind.Stalled, "");
        }

        DeleteRetryMeta(path);
        DeleteRetryMeta(dest);
        _log.Info($"転送成功: {fileName} -> {dest} blob={blobName} {uploaded.Message}");
        return new ProcessOutcome(ProcessKind.Succeeded, dest);
    }

    private List<string> ListPending()
    {
        var list = new List<string>();
        AddCsv(_options.OutputFolder, list);
        AddCsv(_options.RetryFolder, list);

        if (!string.IsNullOrWhiteSpace(_options.ExportFile) && File.Exists(_options.ExportFile))
        {
            var full = Path.GetFullPath(_options.ExportFile);
            if (!list.Any(path => PathEquals(path, full)))
            {
                list.Add(full);
            }
        }

        return list
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCsv(string folder, List<string> list)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.csv"))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(Path.GetFullPath(path));
        }
    }

    private bool TryMoveToProcessed(string source, out string destination, out string error)
    {
        return TryMove(source, _options.ProcessedFolder, out destination, out error);
    }

    private bool TryMoveToRetry(string source, out string destination, out string error)
    {
        var retryFull = Path.GetFullPath(_options.RetryFolder);
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(source));
        if (sourceDir is not null && PathEquals(sourceDir, retryFull))
        {
            destination = Path.GetFullPath(source);
            error = "";
            return true;
        }

        var meta = RetryMetaPath(source);
        if (!TryMove(source, _options.RetryFolder, out destination, out error))
        {
            return false;
        }

        if (File.Exists(meta))
        {
            var metaDest = RetryMetaPath(destination);
            try
            {
                if (File.Exists(metaDest))
                {
                    File.Delete(metaDest);
                }

                File.Move(meta, metaDest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"リトライ情報の移動に失敗: {meta} / {ex.Message}");
            }
        }

        return true;
    }

    private static bool TryMove(string source, string folder, out string destination, out string error)
    {
        try
        {
            Directory.CreateDirectory(folder);
            destination = Path.Combine(folder, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                destination = Path.Combine(
                    folder,
                    Path.GetFileNameWithoutExtension(source) + "_" + stamp + Path.GetExtension(source));
            }

            File.Move(source, destination);
            destination = Path.GetFullPath(destination);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            destination = source;
            error = ex.Message;
            return false;
        }
    }

    private int ReadAttempts(string csvPath)
    {
        var meta = RetryMetaPath(csvPath);
        if (!File.Exists(meta))
        {
            return 0;
        }

        try
        {
            var state = JsonSerializer.Deserialize<RetryState>(File.ReadAllText(meta));
            return state?.Attempts ?? 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn($"リトライ情報を読めません。0 回として扱います: {meta} / {ex.Message}");
            return 0;
        }
    }

    private void WriteAttempts(string csvPath, int attempts, string? lastError)
    {
        var state = new RetryState
        {
            Attempts = attempts,
            LastError = lastError,
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        File.WriteAllText(RetryMetaPath(csvPath), JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void DeleteRetryMeta(string csvPath)
    {
        var meta = RetryMetaPath(csvPath);
        if (File.Exists(meta))
        {
            File.Delete(meta);
        }
    }

    private static string RetryMetaPath(string csvPath) => csvPath + ".retry.json";

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private async Task<IAsyncDisposable?> TryAcquireLockAsync(CancellationToken cancellationToken)
    {
        var logDir = Path.GetDirectoryName(Path.GetFullPath(_options.LogFilePath))
            ?? Path.GetFullPath(_options.OutputFolder);
        Directory.CreateDirectory(logDir);
        var lockPath = Path.Combine(logDir, "transfer.lock");
        var wait = TimeSpan.FromMilliseconds(Math.Max(0, _options.LockWaitMs));
        var started = DateTime.UtcNow;
        var announced = false;
        Exception? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new LockHold(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (DateTime.UtcNow - started >= wait)
                {
                    _log.Warn("他プロセスが実行中のため待機しましたが、ロックを取得できませんでした。ファイルは次回実行で処理します。 " + lastError.Message);
                    return null;
                }

                if (!announced)
                {
                    _log.Info("他プロセスが実行中のため待機します。先行プロセス終了後に処理を開始します。");
                    announced = true;
                }

                var remaining = wait - (DateTime.UtcNow - started);
                var delay = TimeSpan.FromMilliseconds(Math.Min(100, Math.Max(1, remaining.TotalMilliseconds)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private enum ProcessKind
    {
        Succeeded,
        Failed,
        Stalled
    }

    private readonly record struct ProcessOutcome(ProcessKind Kind, string Detail);

    private sealed class RetryState
    {
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private sealed class LockHold : IAsyncDisposable
    {
        private readonly FileStream _stream;

        public LockHold(FileStream stream)
        {
            _stream = stream;
        }

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
