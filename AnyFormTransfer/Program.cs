using System.Text;
using System.Text.Json;

namespace AnyFormTransfer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        return await RunAsync(args).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, IBlobUploader? uploader = null, string? appDirectory = null)
    {
        try
        {
            if (args.Any(arg => arg is "--help" or "-h" or "/?"))
            {
                PrintUsage();
                return 0;
            }

            var options = OptionResolver.Resolve(args, appDirectory);
            using var log = new TransferLog(options.LogFilePath);
            var blob = uploader ?? new LoggingBlobUploader(log);
            var service = new TransferService(options, log, blob);
            var summary = await service.RunAsync().ConfigureAwait(false);
            if (summary.LockTimedOut)
            {
                return 3;
            }

            return summary.Failed > 0 ? 2 : 0;
        }
        catch (TransferConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("エラー: " + ex.Message);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Transfer.exe — AnyForm エクスポート後に実行するプログラム

            AnyForm の「エクスポート後に実行するプログラム」に本 exe を指定する。
            第 1 引数は出力ファイルパス（AnyForm が既定で渡す。追加しない）。
            追加引数を空にした場合は、exe と同じフォルダの transfer.json を読む。

            このビルドは Azure Blob Storage に接続しない。
            対象 CSV をログに記録し、成功分を処理済フォルダ（backup）へ移動する。

            使い方:
              Transfer.exe <出力ファイル.csv>
              Transfer.exe <出力ファイル.csv> --config 別の設定.json

            オプション:
              --config <path>         設定ファイル（JSON）。省略時は exe と同じフォルダの transfer.json
              --intermediate <path>   中間フォルダ（output / backup / retry / log の親）
              --output <path>         転送対象フォルダ（既定: {intermediate}\output）
              --processed <path>      転送成功後の移動先（既定: {intermediate}\backup）
              --retry <path>          転送失敗時の移動先（既定: {intermediate}\retry）
              --log <path>            ログファイル（既定: {intermediate}\log\transfer.log）
              --max-retry <n>         リトライ上限（既定: 3）
              --container <name>      Blob コンテナ名。ログ出力用（既定: ocr）
              --sas <value>           SAS。接続には使わず、ログにも出さない

            終了コード: 0 成功 / 1 引数エラー / 2 転送失敗あり / 3 ロック取得タイムアウト
            """);
    }
}

public static class OptionResolver
{
    public static TransferOptions Resolve(string[] args, string? appDirectory = null)
    {
        var exportFile = Parse(args, out var flags);
        if (!flags.ContainsKey("config"))
        {
            var defaultConfig = Path.Combine(ExeDirectory(appDirectory), "transfer.json");
            if (File.Exists(defaultConfig))
            {
                flags["config"] = defaultConfig;
            }
        }

        TransferConfigFile? config = null;
        if (flags.TryGetValue("config", out var configPath))
        {
            if (!File.Exists(configPath))
            {
                throw new TransferConfigException("設定ファイルが見つかりません: " + configPath);
            }

            config = JsonSerializer.Deserialize<TransferConfigFile>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        string? intermediate = First(flags, "intermediate", config?.IntermediateFolder);
        string? output = First(flags, "output", config?.OutputFolder);
        string? processed = First(flags, "processed", config?.ProcessedFolder);
        string? retry = First(flags, "retry", config?.RetryFolder);
        string? log = First(flags, "log", config?.LogFilePath);

        if (string.IsNullOrWhiteSpace(intermediate)
            && string.IsNullOrWhiteSpace(output)
            && !string.IsNullOrWhiteSpace(exportFile))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exportFile));
            if (string.IsNullOrEmpty(dir))
            {
                throw new TransferConfigException("エクスポートファイルのフォルダを判定できません: " + exportFile);
            }

            if (string.Equals(Path.GetFileName(dir), "output", StringComparison.OrdinalIgnoreCase))
            {
                intermediate = Path.GetDirectoryName(dir);
            }
            else
            {
                output = dir;
            }
        }

        if (!string.IsNullOrWhiteSpace(intermediate))
        {
            intermediate = Path.GetFullPath(intermediate);
            output ??= Path.Combine(intermediate, "output");
            processed ??= Path.Combine(intermediate, "backup");
            retry ??= Path.Combine(intermediate, "retry");
            log ??= Path.Combine(intermediate, "log", "transfer.log");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new TransferConfigException("中間フォルダ（--intermediate / --config）または --output を指定してください。");
        }

        output = Path.GetFullPath(output);
        var parent = Path.GetDirectoryName(output) ?? output;
        processed = Path.GetFullPath(string.IsNullOrWhiteSpace(processed) ? Path.Combine(parent, "backup") : processed);
        retry = Path.GetFullPath(string.IsNullOrWhiteSpace(retry) ? Path.Combine(parent, "retry") : retry);
        log = Path.GetFullPath(string.IsNullOrWhiteSpace(log) ? Path.Combine(parent, "log", "transfer.log") : log);

        var maxRetry = ParseInt(First(flags, "max-retry", config?.MaxRetry?.ToString()), 3, "--max-retry");
        if (maxRetry < 1)
        {
            throw new TransferConfigException("--max-retry は 1 以上にしてください。");
        }

        return new TransferOptions
        {
            OutputFolder = output,
            ProcessedFolder = processed,
            RetryFolder = retry,
            LogFilePath = log,
            Sas = First(flags, "sas", config?.Sas),
            ContainerName = First(flags, "container", config?.ContainerName) ?? "ocr",
            MaxRetry = maxRetry,
            LockWaitMs = ParseInt(First(flags, "lock-wait", config?.LockWaitMs?.ToString()), 60_000, "--lock-wait"),
            RescanDelayMs = ParseInt(First(flags, "rescan-delay", config?.RescanDelayMs?.ToString()), 500, "--rescan-delay"),
            ExportFile = string.IsNullOrWhiteSpace(exportFile) ? null : Path.GetFullPath(exportFile)
        };
    }

    private static string ExeDirectory(string? appDirectory)
    {
        if (!string.IsNullOrWhiteSpace(appDirectory))
        {
            return Path.GetFullPath(appDirectory);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    private static string? Parse(string[] args, out Dictionary<string, string> flags)
    {
        string? exportFile = null;
        flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new TransferConfigException("オプション " + arg + " に値がありません。");
                }

                flags[key] = args[++i];
                continue;
            }

            if (exportFile is not null)
            {
                throw new TransferConfigException("エクスポートファイルは 1 つだけ指定できます: " + arg);
            }

            exportFile = arg;
        }

        return exportFile;
    }

    private static string? First(Dictionary<string, string> flags, string key, string? fallback)
    {
        if (flags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static int ParseInt(string? text, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, out var value))
        {
            throw new TransferConfigException(name + " は整数で指定してください: " + text);
        }

        return value;
    }

    private sealed class TransferConfigFile
    {
        public string? IntermediateFolder { get; set; }
        public string? OutputFolder { get; set; }
        public string? ProcessedFolder { get; set; }
        public string? RetryFolder { get; set; }
        public string? LogFilePath { get; set; }
        public string? Sas { get; set; }
        public string? ContainerName { get; set; }
        public int? MaxRetry { get; set; }
        public int? LockWaitMs { get; set; }
        public int? RescanDelayMs { get; set; }
    }
}
