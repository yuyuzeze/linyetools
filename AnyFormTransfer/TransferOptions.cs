namespace AnyFormTransfer;

public sealed class TransferOptions
{
    /// <summary>AnyForm が CSV を書き出すフォルダ（転送対象）。</summary>
    public required string OutputFolder { get; init; }

    /// <summary>転送成功後の移動先。式样の backup。</summary>
    public required string ProcessedFolder { get; init; }

    /// <summary>転送失敗時の移動先。</summary>
    public required string RetryFolder { get; init; }

    public required string LogFilePath { get; init; }

    /// <summary>ログには出さない。このビルドでは接続に使わない。</summary>
    public string? Sas { get; init; }

    public string ContainerName { get; init; } = "ocr";

    /// <summary>失敗回数の上限。達したファイルはフォルダに残し、今回はスキップする。</summary>
    public int MaxRetry { get; init; } = 3;

    /// <summary>先行プロセスがロックを持っているときの待機時間。</summary>
    public int LockWaitMs { get; init; } = 60_000;

    /// <summary>1 周処理したあと、新規ファイルを再スキャンするまでの待ち。</summary>
    public int RescanDelayMs { get; init; } = 500;

    /// <summary>AnyForm が第 1 引数で渡すエクスポートファイル。未指定ならフォルダスキャンのみ。</summary>
    public string? ExportFile { get; init; }
}

public sealed class TransferSummary
{
    public bool LockTimedOut { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> ProcessedFiles { get; init; } = Array.Empty<string>();
}

public sealed class TransferConfigException : Exception
{
    public TransferConfigException(string message) : base(message)
    {
    }
}
