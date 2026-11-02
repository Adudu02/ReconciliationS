using System.Security.Cryptography;
using Reconciliation.Core.Jobs;

namespace Reconciliation.Core.Ingestion;

public sealed record UploadedFile(string Name, byte[]? Content, byte[] Hash, string? Error = null, int? Length = null, Guid? StoredFileId = null, JobLease? Lease = null)
{
    public int ContentLength => Length ?? Content?.Length ?? 0;
    public ReadOnlyMemory<byte>? ContentMemory => Content is null ? null : Content.AsMemory(0, ContentLength);
    public byte[] RequireContent() => Error is null && Content is not null ? Content : throw new InvalidDataException(Error ?? UploadErrors.MissingContent);
    public static UploadedFile FromBytes(string name, byte[] bytes) => new(name, bytes, SHA256.HashData(bytes));
}
public sealed record FileImportOutcome(Guid FileId, string Name, string Status, string? Reason, Guid? CfdiId = null,
    int ImportedMovements = 0, int DuplicateMovements = 0);
public sealed record ImportReport(Guid UploadId, IReadOnlyList<FileImportOutcome> Files);

public sealed record QueuedImport(Guid JobId, Guid UploadId);
