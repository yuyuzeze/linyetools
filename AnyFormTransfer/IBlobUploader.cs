namespace AnyFormTransfer;

public sealed record BlobUploadRequest(string LocalPath, string BlobName, string? Sas);

public sealed record UploadOutcome(bool Succeeded, string Message);

/// <summary>
/// Azure Blob への転送口。单体测试では接続しない実装を渡す。
/// </summary>
public interface IBlobUploader
{
    Task<UploadOutcome> UploadAsync(BlobUploadRequest request, CancellationToken cancellationToken);
}
