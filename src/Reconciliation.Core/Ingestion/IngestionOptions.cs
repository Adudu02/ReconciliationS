namespace Reconciliation.Core.Ingestion;

public sealed class IngestionOptions
{
    public int MaxFileBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxUploadBytes { get; init; } = 20 * 1024 * 1024;
    public int MaxFiles { get; init; } = 20;
    public int MaxXmlNodes { get; init; } = 100_000;
    public int MaxXmlDepth { get; init; } = 64;
    public int RetentionYears { get; init; } = 10;

    public void Validate()
    {
        if (MaxFileBytes <= 0 || MaxUploadBytes <= 0 || MaxFiles <= 0 || MaxXmlNodes <= 0 || MaxXmlDepth <= 0 || RetentionYears is < 5 or > 100)
            throw new InvalidOperationException("Ingestion limits must be positive and RetentionYears must be between 5 and 100.");
    }
}
