namespace Reconciliation.Core.Ingestion;

public sealed class UploadFailedException(Guid uploadId, Exception cause) : Exception("Upload failed; completed files can be read from its report.", cause)
{
    public Guid UploadId { get; } = uploadId;
}
