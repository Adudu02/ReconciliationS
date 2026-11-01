namespace Reconciliation.Core.Ingestion;

public static class UploadErrors
{
    public const string AggregateLimit = "Upload exceeds file-count or total-size limit.";
    public const string FileLimit = "File exceeds MaxFileBytes.";
    public const string MissingContent = "Missing file bytes.";
    public const string TotalLimit = "Upload exceeds MaxUploadBytes.";
    public const string FileCount = "Upload exceeds MaxFiles.";
}
