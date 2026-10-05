namespace AnyFormTransfer;

/// <summary>
/// Azure Storage には接続しない。対象ファイルをログに残し、成功として返す。
/// </summary>
public sealed class LoggingBlobUploader : IBlobUploader
{
    private readonly TransferLog _log;

    public LoggingBlobUploader(TransferLog log)
    {
        _log = log;
    }

    public async Task<UploadOutcome> UploadAsync(BlobUploadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var stream = new FileStream(
                request.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var size = stream.Length;
            _log.Info(
                $"アップロード（ログのみ、Azure 未接続）: {request.BlobName} <= {request.LocalPath} ({size} bytes)");
            return new UploadOutcome(true, $"{size} bytes");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UploadOutcome(false, ex.Message);
        }
    }
}
