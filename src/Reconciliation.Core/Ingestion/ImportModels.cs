using System.Security.Cryptography;

namespace Reconciliation.Core.Ingestion;

public sealed record UploadedFile(string Name, byte[]? Content, byte[] Hash, string? Error = null)
{
    public static UploadedFile FromBytes(string name, byte[] bytes) => new(name, bytes, SHA256.HashData(bytes));
}
public sealed record FileImportResult(Guid FileId, string Name, string Status, string? Reason, Guid? CfdiId = null,
    int ImportedMovements = 0, int DuplicateMovements = 0);
public sealed record ImportReport(Guid UploadId, IReadOnlyList<FileImportResult> Files);
