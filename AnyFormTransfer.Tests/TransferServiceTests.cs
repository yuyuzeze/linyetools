using System.Text.Json;
using AnyFormTransfer;
using Xunit;

namespace AnyFormTransfer.Tests;

public sealed class TransferServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anyform-transfer-" + Guid.NewGuid().ToString("N"));

    public TransferServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Logs_processed_csv_and_moves_them_to_backup()
    {
        WriteCsv("output", "エクスポート_1.csv", "a,b\n1,10\n");
        WriteCsv("output", "エクスポート_10.csv", "a,b\n1,15\n");
        File.WriteAllText(Path.Combine(OutputDir, "memo.txt"), "ignore");

        var summary = await RunAsync(AlwaysOk());

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.False(File.Exists(Path.Combine(OutputDir, "エクスポート_1.csv")));
        Assert.False(File.Exists(Path.Combine(OutputDir, "エクスポート_10.csv")));
        Assert.True(File.Exists(Path.Combine(BackupDir, "エクスポート_1.csv")));
        Assert.True(File.Exists(Path.Combine(BackupDir, "エクスポート_10.csv")));
        Assert.True(File.Exists(Path.Combine(OutputDir, "memo.txt")));

        var log = ReadLog();
        Assert.Contains("エクスポート_1.csv", log);
        Assert.Contains("エクスポート_10.csv", log);
        Assert.Contains("処理したファイル:", log);
        Assert.Contains("Azure Blob へは接続しません", log);
        Assert.DoesNotContain("sv=2024-secret-token", log);
    }

    [Fact]
    public async Task Failure_moves_to_retry_once_per_run_then_skips_after_limit()
    {
        WriteCsv("output", "bad.csv", "x");
        var uploader = new ScriptedUploader(_ => new UploadOutcome(false, "blob unavailable"));

        var first = await RunAsync(uploader, maxRetry: 2);
        Assert.Equal(1, first.Failed);
        Assert.Equal(0, first.Succeeded);
        Assert.False(File.Exists(Path.Combine(OutputDir, "bad.csv")));
        Assert.True(File.Exists(Path.Combine(RetryDir, "bad.csv")));
        Assert.Equal(1, ReadAttempts(Path.Combine(RetryDir, "bad.csv")));
        Assert.Single(uploader.Calls);

        var second = await RunAsync(uploader, maxRetry: 2);
        Assert.Equal(1, second.Failed);
        Assert.Equal(2, ReadAttempts(Path.Combine(RetryDir, "bad.csv")));

        var third = await RunAsync(uploader, maxRetry: 2);
        Assert.Equal(0, third.Failed);
        Assert.Equal(1, third.Skipped);
        Assert.True(File.Exists(Path.Combine(RetryDir, "bad.csv")));
        Assert.Contains("スキップ", ReadLog());
        Assert.Equal(2, uploader.Calls.Count);
    }

    [Fact]
    public async Task Export_file_outside_output_folder_is_processed()
    {
        var dropped = Path.Combine(_root, "dropped");
        Directory.CreateDirectory(dropped);
        var csv = Path.Combine(dropped, "エクスポート_15.csv");
        File.WriteAllText(csv, "q\n15\n");

        var summary = await RunAsync(AlwaysOk(), exportFile: csv);

        Assert.Equal(1, summary.Succeeded);
        Assert.False(File.Exists(csv));
        Assert.Equal("q\n15\n", File.ReadAllText(Path.Combine(BackupDir, "エクスポート_15.csv")));
    }

    [Fact]
    public async Task Rescan_picks_up_csv_arrived_during_processing()
    {
        WriteCsv("output", "first.csv", "1");
        var uploader = new ScriptedUploader(request =>
        {
            if (Path.GetFileName(request.LocalPath) == "first.csv")
            {
                WriteCsv("output", "second.csv", "2");
            }

            return new UploadOutcome(true, "ok");
        });

        var summary = await RunAsync(uploader);

        Assert.Equal(2, summary.Succeeded);
        Assert.True(File.Exists(Path.Combine(BackupDir, "first.csv")));
        Assert.True(File.Exists(Path.Combine(BackupDir, "second.csv")));
    }

    [Fact]
    public async Task Waits_when_another_process_holds_the_lock_then_gives_up()
    {
        WriteCsv("output", "pending.csv", "1");
        Directory.CreateDirectory(Path.Combine(_root, "log"));
        var lockPath = Path.Combine(_root, "log", "transfer.lock");
        await using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var summary = await RunAsync(AlwaysOk(), lockWaitMs: 200);
            Assert.True(summary.LockTimedOut);
            Assert.True(File.Exists(Path.Combine(OutputDir, "pending.csv")));
            Assert.Contains("ロックを取得できませんでした", ReadLog());
            GC.KeepAlive(held);
        }
    }

    [Fact]
    public async Task Program_reads_config_and_lists_processed_file()
    {
        WriteCsv("output", "エクスポート_1.csv", "h1,h2\n");
        var configPath = Path.Combine(_root, "transfer.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            intermediateFolder = _root,
            sas = "sv=2024-secret-token",
            containerName = "ocr",
            maxRetry = 3,
            rescanDelayMs = 0,
            lockWaitMs = 1000
        }));

        var csv = Path.Combine(OutputDir, "エクスポート_1.csv");
        var code = await Program.RunAsync([csv, "--config", configPath]);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(BackupDir, "エクスポート_1.csv")));
        var log = ReadLog();
        Assert.Contains("エクスポート_1.csv", log);
        Assert.Contains("blob=ocr/エクスポート_1.csv", log);
        Assert.DoesNotContain("sv=2024-secret-token", log);
    }

    [Fact]
    public async Task Program_reads_transfer_json_beside_exe_when_extra_args_are_omitted()
    {
        var exeDir = Path.Combine(_root, "exe");
        Directory.CreateDirectory(exeDir);
        WriteCsv("output", "エクスポート_1.csv", "h1,h2\n");
        await File.WriteAllTextAsync(Path.Combine(exeDir, "transfer.json"), JsonSerializer.Serialize(new
        {
            intermediateFolder = _root,
            containerName = "from-json",
            sas = "sv=2024-secret-token",
            rescanDelayMs = 0,
            lockWaitMs = 1000
        }));

        var csv = Path.Combine(OutputDir, "エクスポート_1.csv");
        var code = await Program.RunAsync([csv], appDirectory: exeDir);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(BackupDir, "エクスポート_1.csv")));
        var log = ReadLog();
        Assert.Contains("blob=from-json/エクスポート_1.csv", log);
        Assert.DoesNotContain("sv=2024-secret-token", log);
    }

    [Fact]
    public async Task Program_returns_1_when_config_is_missing()
    {
        var code = await Program.RunAsync(["--config", Path.Combine(_root, "missing.json")]);
        Assert.Equal(1, code);
    }

    private async Task<TransferSummary> RunAsync(
        IBlobUploader uploader,
        int maxRetry = 3,
        int lockWaitMs = 1000,
        string? exportFile = null)
    {
        var options = new TransferOptions
        {
            OutputFolder = OutputDir,
            ProcessedFolder = BackupDir,
            RetryFolder = RetryDir,
            LogFilePath = Path.Combine(_root, "log", "transfer.log"),
            Sas = "sv=2024-secret-token",
            ContainerName = "ocr",
            MaxRetry = maxRetry,
            LockWaitMs = lockWaitMs,
            RescanDelayMs = 0,
            ExportFile = exportFile
        };

        using var log = new TransferLog(options.LogFilePath);
        var service = new TransferService(options, log, uploader);
        return await service.RunAsync();
    }

    private void WriteCsv(string folderName, string fileName, string contents)
    {
        var dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), contents);
    }

    private string ReadLog() => File.ReadAllText(Path.Combine(_root, "log", "transfer.log"));

    private static int ReadAttempts(string csvPath)
    {
        var json = File.ReadAllText(csvPath + ".retry.json");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Attempts").GetInt32();
    }

    private string OutputDir => Path.Combine(_root, "output");
    private string BackupDir => Path.Combine(_root, "backup");
    private string RetryDir => Path.Combine(_root, "retry");

    private static ScriptedUploader AlwaysOk() =>
        new(_ => new UploadOutcome(true, "ok"));

    private sealed class ScriptedUploader : IBlobUploader
    {
        private readonly Func<BlobUploadRequest, UploadOutcome> _next;

        public ScriptedUploader(Func<BlobUploadRequest, UploadOutcome> next)
        {
            _next = next;
        }

        public List<string> Calls { get; } = new();

        public Task<UploadOutcome> UploadAsync(BlobUploadRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request.LocalPath);
            return Task.FromResult(_next(request));
        }
    }
}
